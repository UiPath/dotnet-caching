using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Redis;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Broadcast;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class DepartedGroupDeleteTests(RedisContainerFixture fixture) : IAsyncLifetime
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.";

    // Fresh is 300 ms, gone is 1 s, so the idle horizon is 1 s.
    private static readonly FormatNegotiationOptions Negotiation = new() { AdvertiseInterval = TimeSpan.FromMilliseconds(100), LivenessWindow = TimeSpan.FromMilliseconds(100) };
    private static readonly TimeSpan Quarantine = TimeSpan.FromMilliseconds(200);

    private readonly CacheOptions _cacheOptions = new() { Enabled = true, AppShortName = $"rp{Guid.NewGuid():N}"[..10] };
    private readonly RecordingTelemetryProvider _telemetry = new();
    private RedisConnector? _connector;
    private RedisKey _stream;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RedisCacheOptions RedisOptions => new() { Enabled = true, FormatNegotiation = Negotiation };

    private IDatabase Db => _connector!.Database;

    private RedisKey NodeListKey => NodeListStore.KeyFor(_cacheOptions, RedisOptions);

    public async ValueTask InitializeAsync()
    {
        if (!fixture.Enabled)
        {
            return;
        }

        var options = Options.Create(new RedisConnectionOptions { ConnectionString = fixture.ConnectionString, EnableHangDetection = false });
        _connector = new RedisConnector(NullTelemetryProvider.Instance, new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options), new ConnectionMultiplexerFactory(options, NullRedisProfiler.Instance), options);
        await _connector.ConnectAsync(Ct);
        _stream = new PrefixStrategy(RedisKeyspaces.Streams, _cacheOptions).GetRedisKey("topic");
        await Db.StreamAddAsync(_stream, "field", "value");
    }

    public async ValueTask DisposeAsync()
    {
        if (_connector is not null)
        {
            await Db.KeyDeleteAsync(_stream);
            await Db.KeyDeleteAsync(NodeListKey);
            _connector.Dispose();
        }
    }

    [Theory]
    [InlineData(false, "stale")]
    [InlineData(true, "left")]
    public async Task A_departed_nodes_caught_up_group_on_a_quiet_stream_is_quarantined_then_deleted(bool left, string reason)
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 2), "Departed groups are judged from Redis 7.2.");
        const string owner = "urn:gone-node";
        await JoinAsync(owner);
        await AdvertiseAsync("urn:alive");
        if (left)
        {
            await AdvertiseAsync(owner, left: true);
        }
        else
        {
            await AdvertiseAsync(owner, ageMs: 5000);
        }

        await PassAsync();
        (await GroupsAsync()).Should().Contain(owner, "the first pass only quarantines");
        await Task.Delay(Quarantine * 2, Ct);
        await AdvertiseAsync("urn:alive");

        await PassAsync();

        (await GroupsAsync()).Should().NotContain(owner);
        _telemetry.Metrics.Should().ContainSingle(m => m.Name == Metrics.DepartedGroupDeleted).Which.Properties!["Reason"].Should().Be(reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Before_Redis_7_2_a_departed_nodes_group_is_kept_and_not_even_quarantined(bool left)
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(fixture.ServerVersion < new Version(7, 2), "Before Redis 7.2 a consumer's idle time ignores read attempts.");
        const string owner = "urn:gone-node";
        await JoinAsync(owner);
        await AdvertiseAsync("urn:alive");
        if (left)
        {
            await AdvertiseAsync(owner, left: true);
        }
        else
        {
            await AdvertiseAsync(owner, ageMs: 5000);
        }

        var maintainer = await PassAsync();
        await Task.Delay(Quarantine * 2, Ct);
        await AdvertiseAsync("urn:alive");
        await PassAsync();

        (await GroupsAsync()).Should().Contain(owner, "an idle time that ignores read attempts cannot protect a live owner that restarted");
        (await Db.HashGetAsync(maintainer.QuarantineKey(_stream), owner)).HasValue.Should().BeFalse("a group that is never judged is never quarantined");
        _telemetry.Metrics.Should().NotContain(m => m.Name == Metrics.DepartedGroupDeleted);
    }

    [Fact]
    public async Task The_node_list_snapshot_is_not_held_after_a_pass()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 2), "Departed groups are judged from Redis 7.2.");
        const string owner = "urn:gone-node";
        await JoinAsync(owner);
        await AdvertiseAsync("urn:alive");
        await AdvertiseAsync(owner, left: true);

        var maintainer = await PassAsync();

        (await Db.HashGetAsync(maintainer.QuarantineKey(_stream), owner)).HasValue.Should().BeTrue("the pass read the node list and judged the group");
        maintainer.HoldsDepartedSnapshot.Should().BeFalse();
    }

    [Fact]
    public async Task A_fresh_nodes_group_is_kept()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        const string owner = "urn:live-node";
        await JoinAsync(owner);

        for (var pass = 0; pass < 3; pass++)
        {
            await AdvertiseAsync(owner);
            await PassAsync();
            await Task.Delay(Quarantine, Ct);
        }

        (await GroupsAsync()).Should().Contain(owner);
        _telemetry.Metrics.Should().NotContain(m => m.Name == Metrics.DepartedGroupDeleted);
    }

    [Theory]
    [InlineData("urn:absent-node")]
    [InlineData("urn:host-p123-abcd1234?stack=uipath.caching.migration")]
    public async Task An_absent_owner_whose_consumers_stayed_idle_is_deleted_in_a_fleet_that_advertises(string owner)
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 2), "Idle time counts read attempts from Redis 7.2.");
        await JoinAsync(owner);
        await Task.Delay(TimeSpan.FromMilliseconds(1300), Ct);

        await AdvertiseAsync("urn:alive");
        await PassAsync();
        await Task.Delay(Quarantine * 2, Ct);
        await AdvertiseAsync("urn:alive");
        await PassAsync();

        (await GroupsAsync()).Should().NotContain(owner);
        _telemetry.Metrics.Should().ContainSingle(m => m.Name == Metrics.DepartedGroupDeleted).Which.Properties!["Reason"].Should().Be("absent_idle");
    }

    [Fact]
    public async Task An_absent_owner_whose_consumer_keeps_polling_past_the_horizon_and_the_quarantine_is_kept()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        const string owner = "urn:silent-but-alive";
        await JoinAsync(owner);
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(1000) + (Quarantine * 4);
        while (DateTime.UtcNow < until)
        {
            await Db.StreamReadGroupAsync(_stream, owner, owner, ">", 1);
            await AdvertiseAsync("urn:alive");
            await PassAsync();
            await Task.Delay(100, Ct);
        }

        (await GroupsAsync()).Should().Contain(owner);
        _telemetry.Metrics.Should().NotContain(m => m.Name == Metrics.DepartedGroupDeleted);
    }

    [Fact]
    public async Task An_owner_that_reads_again_before_the_delete_keeps_its_group()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 2), "Departed groups are judged from Redis 7.2.");
        const string owner = "urn:back-again";
        await JoinAsync(owner);
        await AdvertiseAsync("urn:alive");
        await AdvertiseAsync(owner, ageMs: 5000);
        await PassAsync();
        await Task.Delay(Quarantine * 2, Ct);

        // Its entry is still stale, but it reads the stream again: the delete script sees a consumer idle for less than the quarantine.
        await Db.StreamReadGroupAsync(_stream, owner, owner, ">", 1);
        await AdvertiseAsync("urn:alive");
        await PassAsync();

        (await GroupsAsync()).Should().Contain(owner);
    }

    [Fact]
    public async Task An_absent_owner_is_kept_when_no_node_advertises()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        const string owner = "urn:unknown-node";
        await JoinAsync(owner);
        await Task.Delay(TimeSpan.FromMilliseconds(1300), Ct);

        await PassAsync();
        await Task.Delay(Quarantine * 2, Ct);
        await PassAsync();

        (await GroupsAsync()).Should().Contain(owner, "with nobody advertising, an absent owner cannot be judged");
    }

    [Fact]
    public async Task Nothing_is_deleted_while_the_node_list_cannot_be_read()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        const string owner = "urn:gone-node";
        await JoinAsync(owner);
        await Task.Delay(TimeSpan.FromMilliseconds(1300), Ct);
        await Db.StringSetAsync(NodeListKey, "not a hash");

        await PassAsync();
        await Task.Delay(Quarantine * 2, Ct);
        await PassAsync();

        (await GroupsAsync()).Should().Contain(owner);
        _telemetry.Metrics.Should().NotContain(m => m.Name == Metrics.DepartedGroupDeleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_owner_that_advertises_fresh_between_the_two_reads_keeps_its_group(bool left)
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 2), "Departed groups are judged from Redis 7.2.");
        const string owner = "urn:back-between-reads";
        await JoinAsync(owner);
        await AdvertiseAsync("urn:alive");
        await AdvertiseAsync(owner, left: left, ageMs: left ? 0 : 5000);
        await PassAsync();
        await Task.Delay(Quarantine * 2, Ct);
        await AdvertiseAsync("urn:alive");

        // The owner returns right after the pass's first read of the node list, before the re-check that precedes the delete.
        var reads = 0;
        await PassAsync(before: null, after: (method, _, result) =>
        {
            if (method.Name == nameof(IDatabaseAsync.ScriptEvaluateAsync) && ++reads == 1)
            {
                AdvertiseAsync(owner).GetAwaiter().GetResult();
            }

            return result;
        });

        (await GroupsAsync()).Should().Contain(owner);
        _telemetry.Metrics.Should().NotContain(m => m.Name == Metrics.DepartedGroupDeleted);
    }

    [Fact]
    public async Task A_group_is_kept_when_the_maintainer_lock_no_longer_holds_its_value()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 2), "Departed groups are judged from Redis 7.2.");
        const string owner = "urn:gone-node";
        await JoinAsync(owner);
        await AdvertiseAsync("urn:alive");
        await AdvertiseAsync(owner, left: true);
        await PassAsync();
        await Task.Delay(Quarantine * 2, Ct);
        await AdvertiseAsync("urn:alive");

        await PassAsync(before: (method, _) => method.Name == nameof(IDatabaseAsync.LockQueryAsync) ? (true, Task.FromResult(new RedisValue("another-maintainer"))) : (false, null));

        (await GroupsAsync()).Should().Contain(owner);
        _telemetry.Metrics.Should().NotContain(m => m.Name == Metrics.DepartedGroupDeleted);
    }

    [Fact]
    public async Task An_absent_owner_whose_consumer_just_read_is_not_even_quarantined()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 2), "Idle time counts read attempts from Redis 7.2.");
        const string owner = "urn:silent-but-alive";
        await JoinAsync(owner);
        await AdvertiseAsync("urn:alive");

        var maintainer = await PassAsync();

        (await Db.HashGetAsync(maintainer.QuarantineKey(_stream), owner)).HasValue.Should().BeFalse("an idle consumer is the only evidence that an absent owner left");
    }

    [Fact]
    public async Task Without_scripts_a_left_owners_group_is_deleted_by_the_two_command_fallback()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 2), "Departed groups are judged from Redis 7.2.");
        const string owner = "urn:gone-node";
        await JoinAsync(owner);
        await AdvertiseAsync("urn:alive");
        await AdvertiseAsync(owner, left: true);
        await PassAsync(before: RefuseScripts);
        await Task.Delay(Quarantine * 2, Ct);
        await AdvertiseAsync("urn:alive");

        await PassAsync(before: RefuseScripts);

        (await GroupsAsync()).Should().NotContain(owner);
        _telemetry.Metrics.Should().ContainSingle(m => m.Name == Metrics.DepartedGroupDeleted).Which.Properties!["Reason"].Should().Be("left");
    }

    [Fact]
    public async Task Without_scripts_a_left_owner_that_reads_again_keeps_its_group()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 2), "Departed groups are judged from Redis 7.2.");
        const string owner = "urn:back-again";
        await JoinAsync(owner);
        await AdvertiseAsync("urn:alive");
        await AdvertiseAsync(owner, left: true);
        await PassAsync(before: RefuseScripts);
        await Task.Delay(Quarantine * 2, Ct);
        await Db.StreamReadGroupAsync(_stream, owner, owner, ">", 1);
        await AdvertiseAsync("urn:alive");

        await PassAsync(before: RefuseScripts);

        (await GroupsAsync()).Should().Contain(owner);
    }

    [Fact]
    public async Task Without_scripts_a_stale_entry_is_not_trusted_because_its_age_may_come_from_another_clock()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        Assert.SkipUnless(fixture.ServerVersion >= new Version(7, 2), "Departed groups are judged from Redis 7.2.");
        const string owner = "urn:old-entry";
        await JoinAsync(owner);
        await AdvertiseAsync("urn:alive");
        await AdvertiseAsync(owner, ageMs: 5000);
        await PassAsync(before: RefuseScripts);
        await Task.Delay(Quarantine * 2, Ct);
        await AdvertiseAsync("urn:alive");

        await PassAsync(before: RefuseScripts);

        (await GroupsAsync()).Should().Contain(owner);
        _telemetry.Metrics.Should().NotContain(m => m.Name == Metrics.DepartedGroupDeleted);
    }

    private static (bool Handled, object? Result) RefuseScripts(System.Reflection.MethodInfo method, object?[] args) =>
        method.Name == nameof(IDatabaseAsync.ScriptEvaluateAsync)
            ? (true, Task.FromException<RedisResult>(new RedisCommandException("scripts are disabled")))
            : (false, null);

    private async Task JoinAsync(string node)
    {
        await Db.StreamCreateConsumerGroupAsync(_stream, node, "$");

        // Before Redis 7.0 a read that delivers nothing makes no consumer, and a group without one is deleted as empty, so the consumer is made first.
        await Db.ExecuteAsync("XGROUP", "CREATECONSUMER", _stream, node, node);
        await Db.StreamReadGroupAsync(_stream, node, node, ">", 1);
    }

    private async Task AdvertiseAsync(string node, bool left = false, long ageMs = 0)
    {
        var text = new NodeAdvertisement("test", node, [NodeAdvertisement.Json], new Dictionary<string, IReadOnlyList<string>>(), Left: left).Encode();
        var time = (RedisResult[])(await Db.ExecuteAsync("TIME"))!;
        var now = (long.Parse((string)time[0]!, CultureInfo.InvariantCulture) * 1000) + (long.Parse((string)time[1]!, CultureInfo.InvariantCulture) / 1000);
        await Db.HashSetAsync(NodeListKey, node, string.Create(CultureInfo.InvariantCulture, $"{now - ageMs}|{text}"));
    }

    private async Task<RedisStreamHealthMaintainer> PassAsync(
        Func<System.Reflection.MethodInfo, object?[], (bool Handled, object? Result)>? before = null,
        Func<System.Reflection.MethodInfo, object?[], object?, object?>? after = null)
    {
        IRedisConnector connector = before is null && after is null ? _connector! : new InterceptingConnector(_connector!, before, after);
        var maintainer = new RedisStreamHealthMaintainer(
            connector,
            _telemetry,
            Options.Create(new RedisStreamsTopicOptions { MaintainerEnabled = true, MaintainerQuarantineInterval = Quarantine, MaintainerCheckInterval = TimeSpan.FromMinutes(1) }),
            Options.Create(RedisOptions),
            Options.Create(_cacheOptions),
            NullLogger<RedisStreamHealthMaintainer>.Instance,
            TimeProvider.System);
        maintainer.Initialize();

        // Each pass is a new maintainer; the lock of the last one would still be held for its interval.
        await Db.KeyDeleteAsync(maintainer.LockKey);
        await maintainer.CheckStreamsAsync(Ct);
        return maintainer;
    }

    private async Task<string[]> GroupsAsync() =>
        [.. (await Db.StreamGroupInfoAsync(_stream)).Select(g => g.Name!)];
}
