using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using StackExchange.Redis.Profiling;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.Broadcast;

/// <summary>Two nodes on one Redis, the doorbell on and the poll far away, so only a wake can deliver in time.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class RedisStreamNotifyIntegrationTests(RedisContainerFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan FarPoll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private readonly CacheOptions _cacheOptions = new() { Enabled = true, AppShortName = $"it{Guid.NewGuid():N}"[..10] };
    private readonly IConnectionState _state = Substitute.For<IConnectionState>();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly List<RedisKey> _extraKeys = [];
    // The commands of the nodes' own connection, whatever else runs against the server.
    private readonly ProfilingSession _profile = new();
    private readonly Dictionary<RedisStreamsTopic<ICacheEvent>, TopicRun> _runs = [];
    private RedisConnector? _connector;
    private ConnectionMultiplexer? _admin;
    private RedisKey _stream;
    private RedisChannel _channel;
    private string? _aclUser;
    private DateTimeOffset _now;


    [Fact]
    public async Task A_disposed_connector_ends_the_doorbell_subscribe_retries()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        var connector = await ConnectAsync(fixture.ConnectionString);
        var logger = new RecordingLogger();
        // Due after the connector is gone, as for a host disposed right after it starts.
        var (topic, _) = Topic("urn:a", FarPoll, connector: connector, logger: logger, configure: o => { o.NotifySubscriberDueTime = TimeSpan.FromMilliseconds(200); o.NotifySubscriberTimeout = TimeSpan.FromMilliseconds(100); });
        connector.Dispose();

        await SubscribeAsync(topic);
        _runs[topic].Clock.Advance(TimeSpan.FromMilliseconds(500));

        logger.Records.Count(r => r.Message.Contains("subscribe error", StringComparison.OrdinalIgnoreCase)).Should().BeLessThanOrEqualTo(1, "the retries stop once the connector is gone, rather than one per period");
    }

    [Fact]
    public async Task An_invalidation_wakes_only_the_other_node_for_one_read()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipWhen(fixture.IsCluster, "Counts the commands of one node, and a script cached on one node only is sent again to another.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        var (a, _) = Topic("urn:a", FarPoll);
        var (b, received) = Topic("urn:b", FarPoll);
        await SettledAsync();
        // The server counts a subscription a little before the node treats it as live, and going live wakes a read; the count
        // below starts only once both nodes are live and the reads that woke have ended.
        await ReadyAsync();
        await LiveAsync(a, b);
        (a.NotifyLive && b.NotifyLive).Should().BeTrue("both doorbell subscriptions go live");
        await QuietAsync();

        var read = _runs[b].Reader.NextIdleAsync();
        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();

        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
        await read;
        // StackExchange.Redis records a script call as EVAL, whichever of EVALSHA and EVAL it sent.
        // Before Redis 7.0 the script is not used: the read checks for a gap with an XINFO STREAM sent with the XREADGROUP, against the
        // position it knows; the XINFO GROUPS that asks the server for it ran on the read that went live, within the poll interval.
        // The publisher's XADD and PUBLISH are one script, so they show as one EVAL too.
        string[] expected = fixture.ServerVersion.Major < 7 ? ["EVAL", "XINFO", "XREADGROUP", "XACK"] : ["EVAL", "EVAL", "XACK"];
        _profile.FinishProfiling().Select(c => c.Command).Should().BeEquivalentTo(
            expected,
            "the publisher adds and rings and does not wake for its own ring; the woken node checks for a gap and reads, and acknowledges; on Redis 7 or later the check and read are one script, below 7 one XINFO STREAM goes with a plain XREADGROUP, and a check awaited apart would show as a second XINFO call");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_connector_that_prefixes_its_keys_still_delivers_the_doorbell(bool ringInAdd)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        const string prefix = "tenant:";
        // Deleted after the topics are disposed, so a late group create cannot make it again.
        _extraKeys.Add(_stream.Prepend(prefix));
        var connector = new KeyPrefixingConnector(_connector!, prefix);
        var (a, _) = Topic("urn:a", FarPoll, connector: connector);
        // Off, the doorbell is a PUBLISH of its own; on, it goes out in the script with the XADD.
        a.RingInAdd = ringInAdd;
        var (b, received) = Topic("urn:b", FarPoll, connector: connector);
        await ReadyAsync();
        await LiveAsync(a, b);
        (a.NotifyLive && b.NotifyLive).Should().BeTrue("both doorbell subscriptions go live");
        await QuietAsync();

        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();

        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task On_a_single_primary_the_doorbell_is_sent_behind_the_xadd_and_the_woken_node_finds_the_entry()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipWhen(_connector!.GetPrimaries().Any(p => p.ServerType != ServerType.Standalone), "on a cluster or behind a proxy the doorbell waits for the XADD reply");
        var writes = new HandedBackWrites();
        var (a, _) = Topic("urn:a", FarPoll, pipelines: writes, connector: new KeyPrefixingConnector(_connector!, null, writes));
        // The doorbell as a command of its own, as a sharded or custom channel sends it.
        a.RingInAdd = false;
        var (b, received) = Topic("urn:b", FarPoll);
        await SettledAsync();
        await ReadyAsync();
        await LiveAsync(a, b);
        (a.NotifyLive && b.NotifyLive).Should().BeTrue("both doorbell subscriptions go live");
        await QuietAsync();

        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();

        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
        (await received.Next.Task).Id.Should().Be("1", "the woken node's read finds the entry the doorbell announced");
        await writes.Published!;
        var commands = _profile.FinishProfiling().ToList();
        commands.Should().Contain(c => c.Command == "XADD").And.Contain(c => c.Command == "PUBLISH");
        var add = commands.Single(c => c.Command == "XADD");
        var doorbell = commands.Single(c => c.Command == "PUBLISH");
        doorbell.EndPoint.Should().Be(add.EndPoint, "the doorbell goes to the primary that took the XADD");
        // Ordered against the write handing back the XADD's pending reply, not against the reply's round trip: a first call's
        // JIT or a busy machine can delay the doorbell past a sub-millisecond reply, while ringing after the reply cannot come first.
        writes.BeforeHandback.Should().BeTrue( "the doorbell is enqueued behind the XADD before the publisher awaits its reply");
    }

    [Fact]
    public async Task A_burst_of_this_nodes_own_entries_is_read_before_the_trim_reaches_them()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipWhen(fixture.ServerVersion.Major < 7, "Counts entries with ENTRIESREAD and max-deleted-entry-id, which Redis 7.0 added.");
        // MAXLEN ~ 64 trims whole nodes of 100 entries, so a burst of 300 removes entries the group has not read unless it kept up.
        var log = new RecordingLogger();
        var (a, receivedA) = Topic("urn:a", FarPoll, maxLength: 64, logger: log);
        var (b, _) = Topic("urn:b", FarPoll);
        await SettledAsync();

        // Nothing else reads node a's own entries with the poll far away, so each batch is read only if its 32nd publish woke
        // the reader; a batch is published once the one before it was read, so what the trim removes is always read already.
        for (var batch = 0; batch < 10; batch++)
        {
            var read = _runs[a].Reader.NextIdleAsync();
            for (var i = 0; i < 32; i++)
            {
                (await a.PublishAsync(Event("urn:a", $"{batch}-{i}"), TestContext.Current.CancellationToken)).Should().BeTrue();
            }

            await read;
            (await CaughtUpAsync("urn:a")).Should().BeTrue("every 32 own publishes wake a read (batch {0})", batch);
        }

        (await b.PublishAsync(Event("urn:b", "x"), TestContext.Current.CancellationToken)).Should().BeTrue("its ring makes node a check the stream against its position");
        await receivedA.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
        await ReadyAsync();
        receivedA.Missed.Task.IsCompleted.Should().BeFalse("no entry was trimmed unread: {0}", string.Join(" | ", log.Records.Select(r => $"{r.Level}: {r.Message}")));
    }

    [Fact]
    public async Task A_fresh_node_on_a_stream_of_old_entries_is_not_told_of_a_loss()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        for (var i = 0; i < 5; i++)
        {
            await _connector!.Database.StreamAddAsync(_stream, "event", new TestCacheEventFormatterProxy().Encode(Event("urn:b", $"old-{i}")), $"1-{i}");
        }

        var (_, received) = Topic("urn:fresh", FarPoll);
        await SettledAsync();

        received.Missed.Task.IsCompleted.Should().BeFalse("every entry predates the subscribe, so the group made at the head skipped nothing");
    }

    [Fact]
    public async Task A_fresh_nodes_own_writes_before_its_group_exists_are_not_a_loss()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        await _connector!.Database.StreamAddAsync(_stream, "event", new TestCacheEventFormatterProxy().Encode(Event("urn:b", "old")), "1-0");

        // Published straight after the subscribe, so they race the group the reader makes.
        var (a, received) = Topic("urn:fresh", FarPoll);
        for (var i = 0; i < 20; i++)
        {
            (await a.PublishAsync(Event("urn:fresh", $"own-{i}"), TestContext.Current.CancellationToken)).Should().BeTrue();
        }

        await SettledAsync();
        received.Missed.Task.IsCompleted.Should().BeFalse("this node never acts on its own entries, so skipping them lost nothing");
    }

    [Fact]
    public async Task A_stream_the_server_will_not_describe_is_not_taken_for_a_missing_one()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipUnless(fixture.SupportsAcl, "Needs ACL SETUSER, which this server does not allow.");
        var user = $"it-noxinfo-{Guid.NewGuid():N}"[..20];
        await RedisTestKeys.OnPrimariesAsync(_admin!, "ACL", "SETUSER", user, "on", ">pw", "~*", "&*", "+@all", "-xinfo");
        _aclUser = user;
        RedisKey stream = $"it-noxinfo-{Guid.NewGuid():N}";
        await _admin!.GetDatabase().StreamAddAsync(stream, "f", "v");
        var denied = await ConnectAsync($"{fixture.ConnectionString},user={user},password=pw");

        var head = await ConsumerGroups.HeadAsync(denied.Database, stream);

        head.Should().BeNull("a denied XINFO leaves the head unknown, rather than a missing stream whose group would start at 0-0");
        await _admin!.GetDatabase().KeyDeleteAsync(stream);
    }

    [Fact]
    public async Task A_server_that_refuses_sharded_pubsub_gets_the_doorbell_over_the_literal_channel()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipUnless(fixture.SupportsAcl && fixture.ServerVersion.Major >= 7, "Needs ACL SETUSER and sharded Pub/Sub commands (Redis 7+).");
        var user = $"it-noshard-{Guid.NewGuid():N}"[..20];
        await RedisTestKeys.OnPrimariesAsync(_admin!, "ACL", "SETUSER", user, "on", ">pw", "~*", "&*", "+@all", "-spublish", "-ssubscribe");
        _aclUser = user;
        var limited = await ConnectAsync($"{fixture.ConnectionString},user={user},password=pw");
        var log = new RecordingLogger();
        // The poll is far away, so only a doorbell delivers in time.
        var (a, _) = Topic("urn:a", FarPoll, connector: limited, logger: log, configure: o => o.NotifyShardedPubSub = true);
        var (b, received) = Topic("urn:b", FarPoll, connector: limited, logger: log, configure: o => o.NotifyShardedPubSub = true);

        await ReadyAsync();
        await LiveAsync(a, b);
        (a.NotifyLive && b.NotifyLive).Should().BeTrue("both subscriptions go live on the literal channel after the refusal");
        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();

        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
        log.Records.Count(r => r.Level == LogLevel.Warning && r.Message.Contains("Sharded Pub/Sub was refused", StringComparison.Ordinal)).Should().Be(1, "one warning per connector");
    }

    [Fact]
    public async Task A_subscription_the_server_refuses_leaves_the_topic_polling()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.CountsSubscribersPerClient, "Counts the subscribers of a channel from PUBSUB NUMSUB, which this server does not answer with a client count.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipUnless(fixture.SupportsAcl, "Needs ACL SETUSER, which this server does not allow.");
        var user = $"it-nopubsub-{Guid.NewGuid():N}"[..20];
        await RedisTestKeys.OnPrimariesAsync(_admin!, "ACL", "SETUSER", user, "on", ">pw", "~*", "+@all", "resetchannels");
        _aclUser = user;
        var denied = await ConnectAsync($"{fixture.ConnectionString},user={user},password=pw");
        var log = new RecordingLogger();
        var (a, _) = Topic("urn:a", TimeSpan.FromMilliseconds(250));
        var (b, received) = Topic("urn:b", TimeSpan.FromMilliseconds(250), connector: denied, logger: log);

        await ReadyAsync();
        log.Records.Should().Contain(r => r.Message.Contains("polls only"));
        await ReadyAsync();
        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();

        await AdvanceAsync(b, Poll);
        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
        (await SubscribersAsync()).Should().Be(1, "only node a's connection subscribed");
        var records = string.Join(" | ", log.Records.Select(r => $"{r.Level}: {r.Message}"));
        log.Records.Should().ContainSingle(r => r.Message.Contains("polls only"), "the refusal is logged once: {0}", records);
        // A cold subscription connection may fail once before the server answers with the refusal; that is one warning, not a streak.
        log.Records.Count(r => r.Level == LogLevel.Warning).Should().BeLessThanOrEqualTo(2, "{0}", records);
        log.Records.Should().NotContain(r => r.Level == LogLevel.Error, "{0}", records);
        log.Records.SkipWhile(r => !r.Message.Contains("polls only")).Skip(1).Should().NotContain(r => r.Message.Contains("subscribe"), "nothing is tried again after the refusal: {0}", records);
    }

    [Fact]
    public async Task The_doorbell_survives_a_dropped_subscription_connection()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.CountsSubscribersPerClient, "Counts the subscribers of a channel from PUBSUB NUMSUB, which this server does not answer with a client count.");
        Assert.SkipUnless(fixture.SupportsClientKillByType, "Needs CLIENT KILL TYPE pubsub, which this server does not support.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        var (a, _) = Topic("urn:a", FarPoll);
        var (b, received) = Topic("urn:b", FarPoll);
        await SettledAsync();

        // What a restart or failover looks like from the client: the subscription connection is gone.
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connector!.OnConnectionRestored += (_, _) => restored.TrySetResult();
        await _admin!.GetServer(_admin.GetEndPoints()[0]).ExecuteAsync("CLIENT", "KILL", "TYPE", "pubsub");
        await restored.Task.WaitAsync(TestContext.Current.CancellationToken);
        await ReadyAsync();
        (await SubscribersAsync()).Should().Be(1, "the subscription is taken again");

        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();

        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_live_doorbell_lets_the_poll_wait_the_notified_interval()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        // Node a rings a channel nobody listens on: a doorbell lost while node b's subscription stays live.
        var (a, _) = Topic("urn:a", Poll, configure: o => o.NotifyChannelStrategy = new LostDoorbell());
        // Long enough that a slow wait for the acknowledgement below cannot use up the notified wait before the 600 ms check.
        var (topicB, received) = Topic("urn:b", Poll, configure: o => o.NotifiedPollInterval = TimeSpan.FromSeconds(5));
        await SettledAsync();
        // The waiter is read for the mechanism itself: a primer that merely took long could be a stalled machine, not a live doorbell.
        await LiveAsync(topicB);
        topicB.NotifyLive.Should().BeTrue("the subscription goes live a little after the server counts it");
        // A wait that began before it went live still ends at the 250 ms poll; once that primer is read and acknowledged, the next wait is the notified one.
        (await a.PublishAsync(Event("urn:a", "primer"), TestContext.Current.CancellationToken)).Should().BeTrue();
        await AdvanceAsync(topicB, TimeSpan.FromSeconds(5));
        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
        received.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (await CaughtUpAsync("urn:b")).Should().BeTrue("the primer is read and acknowledged before the wait that follows it begins");

        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();

        _runs[topicB].Clock.Advance(TimeSpan.FromMilliseconds(600));
        received.Next.Task.IsCompleted.Should().BeFalse("the 250 ms poll is not the one in force while the doorbell is live");
        await AdvanceAsync(topicB, TimeSpan.FromMilliseconds(4400));
        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Entries_that_keep_arriving_unannounced_bring_the_poll_interval_back()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        // Node a rings a channel nobody listens on, while node b's subscription stays live.
        var (a, _) = Topic("urn:a", Poll, configure: o => o.NotifyChannelStrategy = new LostDoorbell());
        var log = new RecordingLogger();
        var telemetry = Substitute.For<ICachingTelemetryProvider>();
        var (topicB, received) = Topic("urn:b", Poll, logger: log, configure: o => o.NotifiedPollInterval = TimeSpan.FromSeconds(3), telemetry: telemetry);
        await SettledAsync();
        await LiveAsync(topicB);
        topicB.NotifyLive.Should().BeTrue("the subscription goes live a little after the server counts it");
        // Each of these lands in a wait that began live and runs its whole 3 s, since nothing rings for it.
        foreach (var id in new[] { "primer", "1", "2" })
        {
            (await a.PublishAsync(Event("urn:a", id), TestContext.Current.CancellationToken)).Should().BeTrue();
            await AdvanceAsync(topicB, TimeSpan.FromSeconds(3));
            await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
            received.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
            (await CaughtUpAsync("urn:b")).Should().BeTrue("the entry is read and acknowledged before the wait that follows it begins");
        }

        log.Records.Should().Contain(r => r.Level == LogLevel.Warning && r.Message.Contains("with no doorbell"), "two entries in a row came unannounced");
        topicB.DoorbellShortened.Should().BeTrue();
        telemetry.Received(1).TrackEvent("cache.topic.doorbell_missed", Arg.Any<TelemetryTags<string>>(), Arg.Any<TelemetryTags<double>>());

        (await a.PublishAsync(Event("urn:a", "3"), TestContext.Current.CancellationToken)).Should().BeTrue();

        await AdvanceAsync(topicB, Poll);
        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_nodes_own_entries_read_on_a_timed_wait_are_not_unannounced()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        // No node wakes for its own doorbell, so its own entries are always read on a wait that ran its whole interval.
        var log = new RecordingLogger();
        var (a, _) = Topic("urn:a", Poll, logger: log, configure: o => o.NotifiedPollInterval = TimeSpan.FromSeconds(1));
        await ReadyAsync();
        await LiveAsync(a);
        a.NotifyLive.Should().BeTrue("the subscription goes live");

        foreach (var id in new[] { "1", "2", "3" })
        {
            (await a.PublishAsync(Event("urn:a", id), TestContext.Current.CancellationToken)).Should().BeTrue();
            await AdvanceAsync(a, TimeSpan.FromSeconds(1));
            (await CaughtUpAsync("urn:a")).Should().BeTrue("the node reads and acknowledges its own entry on the next poll");
        }

        a.DoorbellShortened.Should().BeFalse("only entries of other nodes can show a doorbell missing");
        log.Records.Should().NotContain(r => r.Message.Contains("with no doorbell"));
    }

    [Fact]
    public async Task With_the_doorbell_off_the_poll_interval_applies()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        var (a, _) = Topic("urn:a", Poll);
        var (b, received) = Topic("urn:b", Poll, configure: o => { o.NotifyEnabled = false; o.NotifiedPollInterval = FarPoll; });
        await ReadyAsync();

        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();

        await AdvanceAsync(b, Poll);
        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_refused_subscription_leaves_the_poll_interval_in_force()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        Assert.SkipUnless(fixture.SupportsAcl, "Needs ACL SETUSER, which this server does not allow.");
        var user = $"it-nopubsub-{Guid.NewGuid():N}"[..20];
        await RedisTestKeys.OnPrimariesAsync(_admin!, "ACL", "SETUSER", user, "on", ">pw", "~*", "+@all", "resetchannels");
        _aclUser = user;
        var denied = await ConnectAsync($"{fixture.ConnectionString},user={user},password=pw");
        var log = new RecordingLogger();
        var (a, _) = Topic("urn:a", Poll);
        var (b, received) = Topic("urn:b", Poll, connector: denied, logger: log, configure: o => o.NotifiedPollInterval = FarPoll);
        await ReadyAsync();
        log.Records.Should().Contain(r => r.Message.Contains("polls only"));

        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();

        await AdvanceAsync(b, Poll);
        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("1000-0", false, false)]
    [InlineData("1000-0", true, false)]
    [InlineData("1000-5", false, true)]
    [InlineData("1000-5", true, true)]
    public async Task A_recreated_stream_with_a_reused_id_delivers_its_first_entry(string oldId, bool autoAck, bool publishAfterRecovery)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 to run.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        var (b, received) = Topic("urn:b", Poll, configure: o => o.AutoAck = autoAck);
        await SettledAsync();
        var db = _connector!.Database;
        var formatter = new TestCacheEventFormatterProxy();
        // Redis can generate the same id again when DEL and both XADDs fall in one millisecond.
        // Explicit ids make that interleaving deterministic without depending on the machine's speed.
        await db.StreamAddAsync(_stream, "event", formatter.Encode(Event("urn:a", "before")), oldId);
        await AdvanceAsync(b, FarPoll);
        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
        (await CaughtUpAsync("urn:b")).Should().BeTrue();
        received.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await db.KeyDeleteAsync(_stream);
        await db.StreamAddAsync(_stream, "event", formatter.Encode(Event("urn:a", "after")), "1000-0");
        await AdvanceAsync(b, FarPoll);
        await received.Missed.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (publishAfterRecovery)
        {
            // Even writes made after the one-off loss notification are skipped until ids pass the old cursor.
            await db.StreamAddAsync(_stream, "event", formatter.Encode(Event("urn:a", "after-recovery")), "1000-1");
        }
        await AdvanceAsync(b, FarPoll);
        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_stream_deleted_under_two_nodes_is_reported_and_the_next_publish_still_arrives()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipUnless(fixture.CountsSubscribersPerClient, "Counts the subscribers of a channel from PUBSUB NUMSUB, which this server does not answer with a client count.");
        Assert.SkipUnless(fixture.StreamsUsable, RedisContainerFixture.StreamsSkipReason);
        var (a, _) = Topic("urn:a", TimeSpan.FromMilliseconds(250));
        var (b, received) = Topic("urn:b", TimeSpan.FromMilliseconds(250));
        await SettledAsync();
        var firstRead = _runs[b].Reader.NextIdleAsync();
        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();
        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
        await firstRead;
        received.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var recovery = _runs[b].Reader.NextIdleAsync();
        await _connector!.Database.KeyDeleteAsync(_stream);
        (await a.PublishAsync(Event("urn:a", "2"), TestContext.Current.CancellationToken)).Should().BeTrue("the add recreates the stream, without the groups");

        await received.Missed.Task.WaitAsync(TestContext.Current.CancellationToken);
        await recovery;
        await AdvanceAsync(b, FarPoll);
        await received.Next.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask InitializeAsync()
    {
        if (!fixture.Enabled)
        {
            return;
        }

        _connector = await ConnectAsync(fixture.ConnectionString, _profile);
        _admin = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString + ",allowAdmin=true");
        _state.IsConnected.Returns(true);
        var time = (RedisResult[])(await _connector.Database.ExecuteAsync("TIME"))!;
        _now = DateTimeOffset.FromUnixTimeMilliseconds((long)time[0] * 1000 + (long)time[1] / 1000);
        var keys = new PrefixStrategy(RedisKeyspaces.Streams, _cacheOptions);
        _stream = keys.GetRedisKey("orders");
        _channel = new StreamSuffixChannelStrategy(keys, _cacheOptions, null).GetRedisChannel("orders");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (_admin is not null)
        {
            await _admin!.GetDatabase().KeyDeleteAsync(_stream);
            foreach (var key in _extraKeys)
            {
                await _admin!.GetDatabase().KeyDeleteAsync(key);
            }
            if (_aclUser is not null)
            {
                await RedisTestKeys.OnPrimariesAsync(_admin, "ACL", "DELUSER", _aclUser);
            }

            await _admin.DisposeAsync();
        }

        _stop.Dispose();
    }

    private static TestCacheEvent Event(string source, string id) => new() { Id = id, Source = new Uri(source), Data = new("order:1") };

    private async Task<RedisConnector> ConnectAsync(string connectionString, ProfilingSession? profile = null)
    {
        var options = Options.Create(new RedisConnectionOptions
        {
            ConnectionString = connectionString,
            EnableHangDetection = false,
            ProfilerEnabled = profile is not null,
            ProfilingSessionFactory = profile is null ? null : () => profile,
        });
        var connector = new RedisConnector(
            NullTelemetryProvider.Instance,
            new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options),
            new ConnectionMultiplexerFactory(options, NullRedisProfiler.Instance),
            options);
        _disposables.Add(connector);
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        return connector;
    }

    private (RedisStreamsTopic<ICacheEvent> Topic, Received Observer) Topic(string source, TimeSpan pollInterval, long? maxLength = 32_768, IRedisConnector? connector = null, ILogger<RedisStreamsTopic<ICacheEvent>>? logger = null, Action<RedisStreamsTopicOptions>? configure = null, IResiliencePipelineProvider? pipelines = null, ICachingTelemetryProvider? telemetry = null)
    {
        var cacheOptions = new CacheOptions { Enabled = true, AppShortName = _cacheOptions.AppShortName, SourceUri = new Uri(source) };
        var options = new RedisStreamsTopicOptions { Enabled = true, PollInterval = pollInterval, MaxLength = maxLength, NotifySubscriberDueTime = TimeSpan.FromMilliseconds(10) };
        configure?.Invoke(options);
        var clock = new ManualTimeProvider(_now);
        var topic = new RedisStreamsTopic<ICacheEvent>(
            "orders",
            _state,
            connector ?? _connector!,
            () => new KeyedSubject<ICacheEvent>(NullLogger.Instance),
            new TestCacheEventFormatterProxy(),
            pipelines ?? EmptyResiliencePipelineProvider.Instance,
            options,
            cacheOptions,
            logger ?? NullLogger<RedisStreamsTopic<ICacheEvent>>.Instance,
            telemetry ?? NullTelemetryProvider.Instance,
            NullRedisProfiler.Instance,
            clock,
            _stop.Token);
        _runs.Add(topic, new(clock, ObservedFetchWaiter.Attach(topic), options.NotifySubscriberDueTime!.Value, options));
        var observer = new Received();
        _disposables.Add(topic.Subscribe(observer));
        _disposables.Add(topic);
        return (topic, observer);
    }

    private async Task SubscribeAsync(RedisStreamsTopic<ICacheEvent> topic)
    {
        var run = _runs[topic];
        run.Clock.Advance(run.Due);
        var notify = typeof(RedisStreamsTopic<ICacheEvent>).GetField("_notifyChannel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(topic);
        if (notify is not null)
        {
            ((ManualResetEventSlim)notify.GetType().GetField("_subscribingDone", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(notify)!).Wait(TestContext.Current.CancellationToken);
        }
        await Task.CompletedTask;
    }

    private async Task ReadyAsync()
    {
        foreach (var topic in _runs.Keys)
        {
            await SubscribeAsync(topic);
            await SubscribeAsync(topic);
            await _runs[topic].Reader.ReadAsync();
        }
    }

    /// <summary>Waits until each topic's doorbell is live. A failure seen after the subscribe clears on its restore, and a resubscribe it asked for runs on the topic's clock.</summary>
    private async Task LiveAsync(params RedisStreamsTopic<ICacheEvent>[] topics)
    {
        foreach (var topic in topics)
        {
            var run = _runs[topic];
            var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Live() => live.TrySetResult();
            topic.NotifyLiveSet += Live;
            try
            {
                while (!topic.NotifyLive)
                {
                    if (run.Clock.HasTimerDueIn(run.Due))
                    {
                        await SubscribeAsync(topic);
                        continue;
                    }

                    var retry = run.Clock.WhenTimerAsync(() => run.Clock.HasTimerDueIn(run.Due));
                    await Task.WhenAny(live.Task, retry).WaitAsync(TestContext.Current.CancellationToken);
                    // Taken again before the state is read, so a doorbell that went live and dropped again is waited for anew.
                    live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            finally
            {
                topic.NotifyLiveSet -= Live;
            }
        }
    }

    private async Task SettledAsync()
    {
        SkipOnCluster();
        await ReadyAsync();
        (await SubscribersAsync()).Should().Be(1, "one multiplexer carries both nodes' subscriptions");
    }

    private async Task QuietAsync()
    {
        foreach (var run in _runs.Values)
        {
            await run.Reader.ReadAsync();
        }
        _ = _profile.FinishProfiling().ToList();
    }

    private async Task AdvanceAsync(RedisStreamsTopic<ICacheEvent> topic, TimeSpan elapsed)
    {
        var run = _runs[topic];
        var idle = run.Reader.NextIdleAsync();
        run.Clock.Advance(elapsed);
        await idle;
    }

    /// <summary>The group has been delivered the newest entry and acknowledged everything.</summary>
    private async Task<bool> CaughtUpAsync(RedisValue group)
    {
        var db = _admin!.GetDatabase();
        var newest = (await db.StreamInfoAsync(_stream)).LastGeneratedId;
        var info = Array.Find(await db.StreamGroupInfoAsync(_stream), g => g.Name == group);
        return info.Name is not null && info.LastDeliveredId == newest && info.PendingMessageCount == 0;
    }

    private Task<long> SubscribersAsync()
    {
        SkipOnCluster();
        return RedisTestKeys.SubscribersAsync(_admin!, _channel);
    }

    // A cluster keeps a subscription on one node and moves it with the topology, so a count of one is not the count of one connection.
    private void SkipOnCluster() => Assert.SkipWhen(fixture.IsCluster, "Counts the subscribers of one channel, and after a dropped connection StackExchange.Redis 3.4.31 can restore one subscription on two cluster nodes.");

    public class OrderedCalls : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> InvokeCall { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeCall(targetMethod!, args);
    }

    /// <summary>What an application's own connector may do: commands go through a <c>WithKeyPrefix</c> database, subscriptions through the plain subscriber.</summary>
    private sealed class KeyPrefixingConnector(RedisConnector inner, string? prefix, HandedBackWrites? writes = null) : IRedisConnector
    {
        public event EventHandler? OnConnectionFailed
        {
            add => inner.OnConnectionFailed += value;
            remove => inner.OnConnectionFailed -= value;
        }

        public event EventHandler? OnConnectionRestored
        {
            add => inner.OnConnectionRestored += value;
            remove => inner.OnConnectionRestored -= value;
        }

        public event EventHandler? OnReconnected
        {
            add => inner.OnReconnected += value;
            remove => inner.OnReconnected -= value;
        }

        public bool IsConnected => inner.IsConnected;

        public Version Version => inner.Version;

        public IDatabase Database => writes is not null ? writes.Observe(inner.Database) : inner.Database.WithKeyPrefix(prefix!);

        public ISubscriber Subscriber => inner.Subscriber;

        public void ForceReconnect() => inner.ForceReconnect();

        public EndPoint[] GetEndPoints(bool configuredOnly = false) => inner.GetEndPoints(configuredOnly);

        public IEnumerable<IServer> GetPrimaries() => inner.GetPrimaries();

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);

        public void Dispose()
        {
        }
    }

    private sealed class LostDoorbell : IRedisChannelStrategy
    {
        public RedisChannel GetRedisChannel(TopicKey topicKey) => RedisChannel.Literal($"lost:{topicKey}");
    }

    /// <summary>Runs writes as the empty pipeline does, and records when the last one handed its pending result back to the caller.</summary>
    private sealed class HandedBackWrites : IResiliencePipelineProvider, IResiliencePipeline
    {
        private bool _inside;

        public bool BeforeHandback { get; private set; }
        public Task? Published { get; private set; }

        public T Observe<T>(T target) where T : class
        {
            var proxy = DispatchProxy.Create<T, OrderedCalls>();
            ((OrderedCalls)(object)proxy).InvokeCall = (method, args) =>
            {
                if (method.Name == nameof(IDatabase.PublishAsync))
                {
                    BeforeHandback = _inside;
                }
                object? result;
                try
                {
                    result = method.Invoke(target, args);
                }
                catch (TargetInvocationException error) when (error.InnerException is not null)
                {
                    ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                    throw;
                }
                if (method.Name == nameof(IDatabase.PublishAsync))
                {
                    Published = (Task)result!;
                }
                return result switch
                {
                    IConnectionMultiplexer mux => Observe(mux),
                    IDatabase database => Observe(database),
                    _ => result,
                };
            };
            return proxy;
        }

        public IResiliencePipeline Get(string? name) => name == ResiliencePipelineNames.Write ? this : EmptyResiliencePipeline.Instance;

        public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default) =>
            ExecuteAsync(static (c, token) => c(token), callback, defaultValue, cancellationToken);

        public ValueTask<TResult> ExecuteAsync<TResult, TState>(Func<TState, CancellationToken, ValueTask<TResult>> callback, TState state, TResult defaultValue, CancellationToken cancellationToken = default)
        {
            _inside = true;
            try
            {
                return callback(state, cancellationToken);
            }
            finally
            {
                _inside = false;
            }
        }
    }

    private sealed class Received : IObserver<ICacheEvent>, IMissedEventsObserver
    {
        public TaskCompletionSource<ICacheEvent> Next { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Missed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnNext(ICacheEvent value) => Next.TrySetResult(value);

        public void OnEventsMissed(MissedEventsReason reason) => Missed.TrySetResult();

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private sealed class RecordingLogger : ILogger<RedisStreamsTopic<ICacheEvent>>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> _records = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Records => [.. _records];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _records.Enqueue((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
    private sealed record TopicRun(ManualTimeProvider Clock, ObservedFetchWaiter Reader, TimeSpan Due, RedisStreamsTopicOptions Options);
}
