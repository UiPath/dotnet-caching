using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
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
    private readonly CacheOptions _cacheOptions = new() { Enabled = true, AppShortName = $"it{Guid.NewGuid():N}"[..10] };
    private readonly IConnectionState _state = Substitute.For<IConnectionState>();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<IDisposable> _disposables = [];
    // The commands of the nodes' own connection, whatever else runs against the server.
    private readonly ProfilingSession _profile = new();
    private RedisConnector? _connector;
    private ConnectionMultiplexer? _admin;
    private RedisKey _stream;
    private RedisChannel _channel;
    private string? _aclUser;

    [Fact]
    public async Task An_invalidation_wakes_only_the_other_node_for_one_read()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (a, _) = Topic("urn:a", FarPoll);
        var (_, received) = Topic("urn:b", FarPoll);
        await SettledAsync();
        _ = _profile.FinishProfiling().ToList();

        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();

        (await Task.WhenAny(received.Next.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))).Should().BeSameAs(received.Next.Task, "the poll is far away, so only the doorbell delivers in time");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        // StackExchange.Redis records a script call as EVAL, whichever of EVALSHA and EVAL it sent.
        _profile.FinishProfiling().Select(c => c.Command).Should().BeEquivalentTo(
            ["XADD", "PUBLISH", "EVAL", "XACK"],
            "the publisher adds and rings and does not wake for its own ring; the woken node reads with the one script that carries the stream's state, and acknowledges; a check apart would show as XINFO calls");
    }

    [Fact]
    public async Task A_burst_of_this_nodes_own_entries_is_read_before_the_trim_reaches_them()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
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
            for (var i = 0; i < 32; i++)
            {
                (await a.PublishAsync(Event("urn:a", $"{batch}-{i}"), TestContext.Current.CancellationToken)).Should().BeTrue();
            }

            (await EventuallyAsync(() => CaughtUpAsync("urn:a"))).Should().BeTrue("every 32 own publishes wake a read (batch {0})", batch);
        }

        (await b.PublishAsync(Event("urn:b", "x"), TestContext.Current.CancellationToken)).Should().BeTrue("its ring makes node a check the stream against its position");
        (await Task.WhenAny(receivedA.Next.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))).Should().BeSameAs(receivedA.Next.Task);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        receivedA.Missed.Task.IsCompleted.Should().BeFalse("no entry was trimmed unread: {0}", string.Join(" | ", log.Records.Select(r => $"{r.Level}: {r.Message}")));
    }

    [Fact]
    public async Task A_fresh_node_on_a_stream_of_old_entries_is_not_told_of_a_loss()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (b, _) = Topic("urn:b", FarPoll);
        for (var i = 0; i < 5; i++)
        {
            (await b.PublishAsync(Event("urn:b", $"old-{i}"), TestContext.Current.CancellationToken)).Should().BeTrue();
        }

        // Well before the join, so a stall up to here cannot bring the old entries inside the window.
        await Task.Delay(2000, TestContext.Current.CancellationToken);
        var (_, received) = Topic("urn:fresh", FarPoll);
        await SettledAsync();

        received.Missed.Task.IsCompleted.Should().BeFalse("every entry predates the subscribe, so the group made at the head skipped nothing");
    }

    [Fact]
    public async Task A_fresh_nodes_own_writes_before_its_group_exists_are_not_a_loss()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (b, _) = Topic("urn:b", FarPoll);
        (await b.PublishAsync(Event("urn:b", "old"), TestContext.Current.CancellationToken)).Should().BeTrue();
        // Well before the join, so a stall up to the head read cannot bring the other node's entry inside the window.
        await Task.Delay(2000, TestContext.Current.CancellationToken);

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
        var user = $"it-noxinfo-{Guid.NewGuid():N}"[..20];
        await _admin!.GetDatabase().ExecuteAsync("ACL", "SETUSER", user, "on", ">pw", "~*", "&*", "+@all", "-xinfo");
        _aclUser = user;
        RedisKey stream = $"it-noxinfo-{Guid.NewGuid():N}";
        await _admin.GetDatabase().StreamAddAsync(stream, "f", "v");
        var denied = await ConnectAsync($"{fixture.ConnectionString},user={user},password=pw");

        var head = await ConsumerGroups.HeadAsync(denied.Database, stream);

        head.Should().BeNull("a denied XINFO leaves the head unknown, rather than a missing stream whose group would start at 0-0");
        await _admin.GetDatabase().KeyDeleteAsync(stream);
    }

    [Fact]
    public async Task A_subscription_the_server_refuses_leaves_the_topic_polling()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var user = $"it-nopubsub-{Guid.NewGuid():N}"[..20];
        await _admin!.GetDatabase().ExecuteAsync("ACL", "SETUSER", user, "on", ">pw", "~*", "+@all", "resetchannels");
        _aclUser = user;
        var denied = await ConnectAsync($"{fixture.ConnectionString},user={user},password=pw");
        var log = new RecordingLogger();
        var (a, _) = Topic("urn:a", TimeSpan.FromMilliseconds(250));
        var (_, received) = Topic("urn:b", TimeSpan.FromMilliseconds(250), connector: denied, logger: log);

        (await EventuallyAsync(() => Task.FromResult(log.Records.Any(r => r.Message.Contains("polls only"))))).Should().BeTrue();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();

        (await Task.WhenAny(received.Next.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))).Should().BeSameAs(received.Next.Task, "the poll still delivers");
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
        var (a, _) = Topic("urn:a", FarPoll);
        var (_, received) = Topic("urn:b", FarPoll);
        await SettledAsync();

        // What a restart or failover looks like from the client: the subscription connection is gone.
        await _admin!.GetServer(_admin.GetEndPoints()[0]).ExecuteAsync("CLIENT", "KILL", "TYPE", "pubsub");
        (await EventuallyAsync(async () => await SubscribersAsync() == 1, TimeSpan.FromSeconds(20))).Should().BeTrue("the subscription is taken again");

        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();

        (await Task.WhenAny(received.Next.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))).Should().BeSameAs(received.Next.Task);
    }

    [Fact]
    public async Task A_stream_deleted_under_two_nodes_is_reported_and_the_next_publish_still_arrives()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var (a, _) = Topic("urn:a", TimeSpan.FromMilliseconds(250));
        var (_, received) = Topic("urn:b", TimeSpan.FromMilliseconds(250));
        await SettledAsync();
        (await a.PublishAsync(Event("urn:a", "1"), TestContext.Current.CancellationToken)).Should().BeTrue();
        (await Task.WhenAny(received.Next.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))).Should().BeSameAs(received.Next.Task);
        received.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await _connector!.Database.KeyDeleteAsync(_stream);
        (await a.PublishAsync(Event("urn:a", "2"), TestContext.Current.CancellationToken)).Should().BeTrue("the add recreates the stream, without the groups");

        (await Task.WhenAny(received.Missed.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))).Should().BeSameAs(received.Missed.Task, "a wake read that finds no group reports what the group held as lost");
        (await Task.WhenAny(received.Next.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))).Should().BeSameAs(received.Next.Task, "the group is recreated and the entry read at the next poll");
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
            await _admin.GetDatabase().KeyDeleteAsync(_stream);
            if (_aclUser is not null)
            {
                await _admin.GetDatabase().ExecuteAsync("ACL", "DELUSER", _aclUser);
            }

            _admin.Dispose();
        }

        _stop.Dispose();
    }

    private static TestCacheEvent Event(string source, string id) => new() { Id = id, Source = new Uri(source), Data = new("order:1") };

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return false;
    }

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

    private (RedisStreamsTopic<ICacheEvent> Topic, Received Observer) Topic(string source, TimeSpan pollInterval, long? maxLength = 32_768, RedisConnector? connector = null, ILogger<RedisStreamsTopic<ICacheEvent>>? logger = null)
    {
        var cacheOptions = new CacheOptions { Enabled = true, AppShortName = _cacheOptions.AppShortName, SourceUri = new Uri(source) };
        var options = new RedisStreamsTopicOptions { Enabled = true, PollInterval = pollInterval, MaxLength = maxLength, NotifySubscriberDueTime = TimeSpan.FromMilliseconds(10) };
        var topic = new RedisStreamsTopic<ICacheEvent>(
            "orders",
            _state,
            connector ?? _connector!,
            () => new KeyedSubject<ICacheEvent>(NullLogger.Instance),
            new TestCacheEventFormatterProxy(),
            EmptyResiliencePipelineProvider.Instance,
            options,
            cacheOptions,
            logger ?? NullLogger<RedisStreamsTopic<ICacheEvent>>.Instance,
            NullTelemetryProvider.Instance,
            NullRedisProfiler.Instance,
            _stop.Token);
        var observer = new Received();
        _disposables.Add(topic.Subscribe(observer));
        _disposables.Add(topic);
        return (topic, observer);
    }

    // Both nodes subscribed to the doorbell, and the reads their subscriptions trigger done.
    private async Task SettledAsync()
    {
        (await EventuallyAsync(async () => await SubscribersAsync() == 1)).Should().BeTrue("one multiplexer carries both nodes' subscriptions");
        await Task.Delay(300, TestContext.Current.CancellationToken);
    }

    /// <summary>The group has been delivered the newest entry and acknowledged everything.</summary>
    private async Task<bool> CaughtUpAsync(RedisValue group)
    {
        var db = _admin!.GetDatabase();
        var newest = (await db.StreamInfoAsync(_stream)).LastGeneratedId;
        var info = Array.Find(await db.StreamGroupInfoAsync(_stream), g => g.Name == group);
        return info.Name is not null && info.LastDeliveredId == newest && info.PendingMessageCount == 0;
    }

    private async Task<long> SubscribersAsync() =>
        (long)(await _admin!.GetDatabase().ExecuteAsync("PUBSUB", "NUMSUB", _channel.ToString()))[1];

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
}
