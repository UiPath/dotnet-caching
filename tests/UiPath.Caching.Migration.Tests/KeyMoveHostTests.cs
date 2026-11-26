using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests;

// The key move node in the router build: nothing of it exists without UseKeyMove, and with it the tokens, the snapshot, the lease and the poller come up with the library and go down with it.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHostTests(RedisFixture fixture) : IAsyncLifetime
{
    private const string Skip = "Set RUN_REDIS_INTEGRATION_TESTS=1 (Redis at 127.0.0.1:6379) to run.";
    private const string RouterAppShortName = "kmhostrouter";
    private const char SharedRouterSeparator = '$';

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan AckInterval = TimeSpan.FromMilliseconds(53);
    private static readonly TimeSpan AdvertiseInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DriverInterval = TimeSpan.FromMilliseconds(2300);
    private static readonly TimeSpan LeaseInterval = TimeSpan.FromMilliseconds(211);
    private static readonly TimeSpan RewireInterval = TimeSpan.FromMilliseconds(500);

    private readonly string _unique = Guid.NewGuid().ToString("N")[..10];
    private readonly ConcurrentQueue<KeyMoveAlert> _alerts = new();
    private readonly PollSchedule _snapshots = new();
    private readonly PollSchedule _flags = new();
    private readonly Channel<StepOutcome?> _steps = Channel.CreateUnbounded<StepOutcome?>();
    private ConnectionMultiplexer? _redis;
    private DateTimeOffset _serverTime;
    private RedisKey? _nodeKey;
    private string? _field;
    private RedisKey? _isolatedNodeKey;
    private RedisPinStore? _startedGroupPins;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => _redis!.GetDatabase();

    private string Group => "kmh" + _unique;

    private string Library => $"kmh{_unique}:";

    public async ValueTask InitializeAsync()
    {
        if (fixture.Enabled)
        {
            _redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
            _serverTime = await _redis.GetServer(_redis.GetEndPoints()[0]).TimeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_redis is null)
        {
            return;
        }

        await using var redis = _redis;
        var names = new MoveKeyNames(string.Empty, Library, cluster: false);
        await Db.KeyDeleteAsync([names.State(Group), names.Lead(Group), .. names.ControlRecords(Group).Select(record => record.Key)]);
        if (_nodeKey is { } key && _field is not null)
        {
            await Db.HashDeleteAsync(key, _field);
        }

        if (_isolatedNodeKey is { } isolatedNodeKey)
        {
            await Db.KeyDeleteAsync(isolatedNodeKey);
        }

        if (_startedGroupPins is { } pins)
        {
            await Db.KeyDeleteAsync([pins.StateKey(Group), pins.AckKey(Group)]);
            var server = redis.GetServers().Single();
            await foreach (var remaining in server.KeysAsync(Db.Database, pattern: $"*{Group}*").WithCancellation(Ct))
            {
                Assert.Fail($"Redis still contains the group's key {remaining}.");
            }
        }
    }

    [Fact]
    public async Task Without_UseKeyMove_nothing_runs_nothing_is_advertised_and_no_key_of_the_move_is_written()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(keyMove: false);
        var router = (Router)provider.GetRequiredService<IRouter>();

        var entry = await EntryAsync(router, provider);

        router.KeyMove.Should().BeNull();
        provider.GetService<IMoveRate>().Should().BeNull();
        router.Library!.Started.Should().NotContain(s => s is MoverHostedService);
        entry.Tokens.Should().NotContain(t => t == "km" || t.StartsWith("kmg=", StringComparison.Ordinal) || t.StartsWith("snap=", StringComparison.Ordinal) || t.StartsWith("fl=", StringComparison.Ordinal));
        (await Db.KeyExistsAsync($"{Library}mg:{{{Group}}}:lead")).Should().BeFalse();
    }

    [Fact]
    public async Task A_setup_without_a_group_adds_nothing_to_the_entry_and_starts_nothing()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(keyMove: true, groups: []);
        var router = (Router)provider.GetRequiredService<IRouter>();

        var entry = await EntryAsync(router, provider);

        router.KeyMove!.HasGroups.Should().BeFalse();
        router.KeyMove.Runtime.Should().BeNull();
        entry.Tokens.Should().NotContain("km");
    }

    [Fact]
    public async Task With_a_listed_group_the_node_advertises_the_key_move_tokens_and_the_snapshot_age_holds_the_lease_polls_the_flag_and_stops_with_the_router()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(keyMove: true);
        var router = (Router)provider.GetRequiredService<IRouter>();
        var lead = $"{Library}mg:{{{Group}}}:lead";

        await RouterTestBase.WaitForLibraryAsync(router, Ct);
        var move = router.KeyMove!;
        move.Runtime.Should().NotBeNull("the node starts with the library");
        var clock = (SignalingTimeProvider)router.Clock;
        await AdvanceLoopAsync(clock, LeaseInterval);
        (await Db.KeyExistsAsync(lead)).Should().BeTrue("a relevant group is led by whoever holds the lease");

        await AdvancePollsAsync(clock);
        await AdvanceLoopAsync(clock, AdvertiseInterval);
        var tokens = (await ReadEntryAsync(router, provider)).Tokens;
        tokens.Should().Contain("km").And.Contain($"kmg={Group}").And.Contain($"ca={Group}:advc");
        tokens.Should().Contain(t => t.StartsWith("snap=", StringComparison.Ordinal), "a listed group has a snapshot");
        var init = await move.Runtime.Commands.InitAsync(Group, ["advc"], Ct);
        init.Code.Should().Be(MoveScripts.StateCreated);
        await AdvancePollsAsync(clock);
        clock.Advance(AdvertiseInterval);
        await clock.WaitForTimerAsync(AdvertiseInterval, Ct);
        (await ReadEntryAsync(router, provider)).Tokens.Should().Contain($"fl={Group}:2", "the stage flag is polled once the group is Init-ed and listed");

        await router.StopAsync(Ct);

        move.Runtime.Should().BeNull();
        (await Db.KeyExistsAsync(lead)).Should().BeFalse("a stopped node releases its lease");
    }

    [Fact]
    public async Task A_node_acknowledges_the_identity_of_an_Init_ed_group_it_acts_under()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(keyMove: true);
        var router = (Router)provider.GetRequiredService<IRouter>();
        await RouterTestBase.WaitForLibraryAsync(router, Ct);
        router.KeyMove!.Runtime.Should().NotBeNull();

        await router.KeyMove!.Runtime!.Commands.InitAsync(Group, ["advc"], Ct);
        var state = (await router.KeyMove.Runtime.Store.ReadAsync(Group, Ct))!;

        var clock = (SignalingTimeProvider)router.Clock;
        await AdvancePollsAsync(clock);
        await AdvanceLoopAsync(clock, AckInterval);
        await AdvanceLoopAsync(clock, AdvertiseInterval);
        (await ReadEntryAsync(router, provider)).Tokens.Should().Contain(t => t.StartsWith($"{Group}@{state.Ep}:{state.Id}:S0", StringComparison.Ordinal));
    }

    // A node of a build without key move has no km token in its advert: once it joins a group that has started, the leader holds the group, and the hold ends when it leaves.
    [Fact]
    public async Task A_node_without_key_move_that_joins_after_Start_holds_the_group_and_its_leaving_ends_the_hold()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        var clock = new DriverTestClock();
        var separator = await ReserveAdvertisementSeparatorAsync();
        await using var leader = Provider(keyMove: true, node: "a", startable: true, clock: clock, separator: separator);
        var router = (Router)leader.GetRequiredService<IRouter>();
        var runtime = router.KeyMove!.Runtime!;
        await runtime.Driver!.StopAsync();
        await clock.ReadyAsync();
        var adverts = AdvertisementStore(leader);
        _startedGroupPins = new RedisPinStore(adverts, string.Empty, ScopeTokens.For(RouterAppShortName));
        await runtime.Commands.InitAsync(Group, ["advc"], Ct);
        (await runtime.Commands.ApproveAsync(Group, ApprovalKinds.StartNoDryRun, "test", "test", Ct)).Should().Be(ApproveResult.Stored);
        (await runtime.Commands.ApproveAsync(Group, ApprovalKinds.StartMemory, "test", "test", Ct)).Should().Be(ApproveResult.Stored);

        (await router.Advertiser!.WriteAsync(Ct)).Should().BeTrue();
        var started = await StepAsync(runtime, clock);
        started.Should().Match<StateRecord>(state => state.State != "S0" && state.Durable,
            "every start gate passes for one node with key move; the driver said: " + string.Join("; ", _alerts.Select(a => $"{a.Code} {a.Detail}").Distinct()));
        started.HoldAuto.Should().BeEmpty();
        await AssertForeignAdvertDoesNotHoldAsync(runtime, clock);

        string joinedField;
        await using (var nodeWithoutKeyMove = Provider(keyMove: false, node: "b", clock: clock, separator: separator))
        {
            var joined = (Router)nodeWithoutKeyMove.GetRequiredService<IRouter>();
            (await joined.Advertiser!.WriteAsync(Ct)).Should().BeTrue();
            joinedField = joined.Advertiser.Field;
            (await StepAsync(runtime, clock)).HoldAuto.Should().StartWith("no_km:").And.Contain("kmh" + _unique + "b");

            for (var pass = 0; pass < 6; pass++)
            {
                var held = await StepAsync(runtime, clock);
                held.HoldAuto.Should().StartWith("no_km:");
                held.State.Should().NotBe("S0");
            }
        }

        var entries = await adverts.ReadAsync(Ct);
        entries.Should().Contain(entry => entry.Field == joinedField && entry.Advertisement.Left);
        var released = await StepAsync(runtime, clock);
        released.HoldAuto.Should().BeEmpty("the node without key move left");
        released.State.Should().NotBe("S0");
    }

    [Fact]
    public async Task After_a_reconnect_the_node_reads_state_renews_its_lease_and_runs_commands_over_the_new_connection()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(keyMove: true);
        var router = (Router)provider.GetRequiredService<IRouter>();
        var connector = provider.GetRequiredService<IRedisConnector>();
        await RouterTestBase.WaitForLibraryAsync(router, Ct);
        router.KeyMove!.Runtime.Should().NotBeNull();
        await router.KeyMove!.Runtime!.Commands.InitAsync(Group, ["advc"], Ct);
        var before = router.KeyMove.Runtime;
        var oldMux = connector.Database.Multiplexer;
        var lead = $"{Library}mg:{{{Group}}}:lead";
        var clock = (SignalingTimeProvider)router.Clock;
        await clock.WaitForTimerAsync(LeaseInterval, Ct);
        (await Db.KeyExistsAsync(lead)).Should().BeTrue();

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Reconnected(object? sender, EventArgs args) => reconnected.TrySetResult();
        connector.OnReconnected += Reconnected;
        try
        {
            connector.ForceReconnect();
            await reconnected.Task.WaitAsync(Ct);
        }
        finally
        {
            connector.OnReconnected -= Reconnected;
        }

        connector.Database.Multiplexer.Should().NotBeSameAs(oldMux);
        await clock.WaitForTimerAsync(RewireInterval, Ct);
        clock.Advance(RewireInterval);
        await clock.WaitForTimerAsync(RewireInterval, Ct);
        router.KeyMove.Runtime.Should().NotBeSameAs(before, "the node rebuilt its services over the new connection");
        await Db.KeyDeleteAsync(lead);
        await clock.WaitForTimerAsync(LeaseInterval, Ct);
        clock.Advance(LeaseInterval);
        await clock.WaitForTimerAsync(LeaseInterval, Ct);

        (await Db.KeyExistsAsync(lead)).Should().BeTrue("the lease is taken again through the new connection");
        (await router.KeyMove.Runtime!.Store.ReadAsync(Group, Ct)).Should().NotBeNull("commands run over the new connection");

        await AdvancePollsAsync(clock);
        router.KeyMove.Monitor.CurrentState(Group).Should().NotBeNull("the snapshot reads through the replacement connection");
        router.KeyMove.Monitor.SnapshotAge().Should().Be(TimeSpan.Zero);
        await AdvanceLoopAsync(clock, AdvertiseInterval);
        (await ReadEntryAsync(router, provider)).Tokens.Should().Contain(t => t == "snap=0" || t == "snap=5", "the advertisement carries the refreshed snapshot");
    }

    [Fact]
    public async Task A_lease_release_that_throws_does_not_keep_the_node_from_stopping_all_of_its_parts()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(keyMove: true);
        var router = (Router)provider.GetRequiredService<IRouter>();
        var connector = provider.GetRequiredService<IRedisConnector>();
        await RouterTestBase.WaitForLibraryAsync(router, Ct);
        router.KeyMove!.Runtime.Should().NotBeNull();

        // Every command of the node now fails, the lease release included.
        await connector.Database.Multiplexer.DisposeAsync();
        await router.KeyMove!.StopAsync();

        router.KeyMove.Runtime.Should().BeNull();
        await router.KeyMove.StopAsync();
    }

    private static async Task AdvanceLoopAsync(SignalingTimeProvider clock, TimeSpan interval)
    {
        await clock.WaitForTimerAsync(interval, Ct);
        clock.Advance(interval);
        await clock.WaitForTimerAsync(interval, Ct);
    }

    private static RedisKey AdvertisementKey(char separator) => RouterServiceCollectionExtensions.NodeListKey(
        new CacheOptions { Separator = separator },
        new RedisCacheOptions { RedisKeyStrategyFactory = PassThroughKeyStrategyFactory.Instance });

    private async Task AdvancePollsAsync(SignalingTimeProvider clock)
    {
        // Both polls share an interval, so each loop signals when it arms its own timer.
        await _snapshots.WaitAsync();
        await _flags.WaitAsync();
        clock.Advance(PollInterval);
        await _snapshots.WaitAsync();
        await _flags.WaitAsync();
    }

    private async Task<StateRecord> StepAsync(KeyMoveRuntime runtime, DriverTestClock clock)
    {
        clock.Advance();
        (await runtime.Driver!.StepAsync(Group, Ct)).Should().NotBeNull("the router leads the group");
        return (await runtime.Store.ReadAsync(Group, Ct))!;
    }

    private async Task AssertForeignAdvertDoesNotHoldAsync(KeyMoveRuntime runtime, DriverTestClock clock)
    {
        var sharedKey = AdvertisementKey(SharedRouterSeparator);
        var sharedAdverts = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), sharedKey);
        var foreignNode = $"foreign-{Group}";
        var field = foreignNode + RouterAdvertisement.FieldSuffix;
        try
        {
            await sharedAdverts.WriteAsync(field, new RouterAdvertisement(foreignNode, "test", []), Ct);
            (await StepAsync(runtime, clock)).HoldAuto.Should().BeEmpty("the foreign node advertises to the shared hash");
        }
        finally
        {
            await Db.HashDeleteAsync(sharedKey, field);
        }
    }

    private async Task<char> ReserveAdvertisementSeparatorAsync()
    {
        // The separator is one character. Reserve a private-use character atomically across test runners sharing Redis.
        for (var separator = '\uE000'; separator <= '\uF8FF'; separator++)
        {
            var key = AdvertisementKey(separator);
            if (await Db.HashSetAsync(key, "test-reservation", Group, When.NotExists))
            {
                _isolatedNodeKey = key;
                return separator;
            }
        }

        throw new InvalidOperationException("No test advertisement separator is available.");
    }

    private RouterAdvertisementStore AdvertisementStore(ServiceProvider provider)
    {
        var cacheOptions = provider.GetRequiredKeyedService<IOptions<CacheOptions>>(RouterServiceCollectionExtensions.StackName).Value;
        var redisOptions = provider.GetRequiredKeyedService<IOptions<RedisCacheOptions>>(RouterServiceCollectionExtensions.StackName).Value;
        return new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), RouterServiceCollectionExtensions.NodeListKey(cacheOptions, redisOptions));
    }

    private async Task<RouterAdvertisement> EntryAsync(Router router, ServiceProvider provider)
    {
        (await router.Advertiser!.WriteAsync(Ct)).Should().BeTrue();
        return await ReadEntryAsync(router, provider);
    }

    private async Task<RouterAdvertisement> ReadEntryAsync(Router router, ServiceProvider provider)
    {
        var cacheOptions = provider.GetRequiredKeyedService<IOptions<CacheOptions>>(RouterServiceCollectionExtensions.StackName).Value;
        var redisOptions = provider.GetRequiredKeyedService<IOptions<RedisCacheOptions>>(RouterServiceCollectionExtensions.StackName).Value;
        _nodeKey = RouterServiceCollectionExtensions.NodeListKey(cacheOptions, redisOptions);
        _field = router.Advertiser!.Field;
        var store = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), _nodeKey.Value);
        return (await store.ReadAsync(Ct)).Single(e => e.Field == _field).Advertisement;
    }

    // A startable node passes every start gate: the flag asks for 3, the facts hold, and the view needs no time to settle.
    private ServiceProvider Provider(bool keyMove, IReadOnlyList<GroupRegistration>? groups = null, string node = "", bool startable = false, DriverTestClock? clock = null, char separator = SharedRouterSeparator)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = "kmhost",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            // The preflight of a start reads the eviction policy, which needs an admin connection.
            ["Caching:Connections:Redis:ConnectionString"] = startable ? fixture.ConnectionString + ",allowAdmin=true" : fixture.ConnectionString,
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["RouterCaching:AppShortName"] = RouterAppShortName,
            ["RouterCaching:Separator"] = separator.ToString(),
            ["RouterCaching:SourceUri"] = "urn:km-host",
            ["RouterCaching:Redis:FormatNegotiation:LivenessWindow"] = "00:00:30",
            ["RouterCaching:Redis:FormatNegotiation:AdvertiseInterval"] = "00:00:01",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var source = new LegacySource(_redis!, "advc");
        var services = new ServiceCollection().AddLogging()
            .AddSingleton<TimeProvider>(clock ?? (TimeProvider)new SignalingTimeProvider(_serverTime));

        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        services.AddCachingRouter(
            configuration,
            router =>
            {
                router
                    .UseLibraryWhen(_ => true)
                    .UseEnvelope(new LegacyEnvelopeProxy())
                    .UseNodeIdentity("kmh" + _unique + node)
                    .UseSourceConnection(() => Db)
                    .Add(new RouterCacheRegistration<List<long>> { Name = "advc", Group = Group, Source = source, RedisKey = source.RedisKey });
                if (startable)
                {
                    // A group starts only while every node pins it to the library.
                    router.UsePinWhen(_ => true);
                }

                if (keyMove)
                {
                    router.UseKeyMoveSetup(new KeyMoveSetup(groups ?? [new GroupRegistration(Group, false, true, ["advc"])], new FixedStage(startable ? 3 : 2), new Facts(startable), new Space(), new NoClaim(), Library)
                    {
                        State = new KeyMoveStateOptions
                        {
                            ExpectedReplicasPerMaster = 0,
                            LeaseRenewInterval = clock is null ? LeaseInterval : DriverTestClock.StartupInterval,
                            PollInterval = clock is null ? PollInterval : DriverTestClock.StartupInterval,
                        },
                        Alert = _alerts.Enqueue,
                        SnapshotPollScheduled = _snapshots.Scheduled,
                        StagePollScheduled = _flags.Scheduled,
                        DriverStepped = step => _steps.Writer.TryWrite(step),
                        Driver = clock is null ? new StageDriverOptions
                        {
                            AckInterval = AckInterval,
                            StepInterval = DriverInterval,
                            UpgradeHold = startable ? TimeSpan.Zero : new StageDriverOptions().UpgradeHold,
                        }
                            : startable
                                ? new StageDriverOptions { AckInterval = TimeSpan.FromMilliseconds(50), UpgradeHold = TimeSpan.Zero }
                                : new StageDriverOptions { AckInterval = TimeSpan.FromMilliseconds(50) },
                    });
                }
            },
            "RouterCaching");
        return services.BuildServiceProvider();
    }

    private sealed class DriverTestClock : TimeProvider
    {
        public static readonly TimeSpan StartupInterval = TimeSpan.FromMilliseconds(200);

        private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startupTimers;

        public override long TimestampFrequency => _time.TimestampFrequency;

        public override long GetTimestamp() => _time.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();

        public void Advance() => _time.Advance(TimeSpan.FromMilliseconds(250));

        public Task ReadyAsync() => _ready.Task.WaitAsync(Ct);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _time.CreateTimer(callback, state, dueTime, period);
            // The flag read arms a timeout. The flag, snapshot and lease loops each arm a delay after their first pass.
            if (dueTime == StartupInterval && Interlocked.Increment(ref _startupTimers) == 4)
            {
                _ready.TrySetResult();
            }

            return timer;
        }
    }

    private sealed class PollSchedule
    {
        private readonly Channel<Task> _scheduled = Channel.CreateUnbounded<Task>();
        private Task _delay = Task.CompletedTask;

        public void Scheduled(Task delay) => _scheduled.Writer.TryWrite(delay);

        public async Task WaitAsync()
        {
            while (_delay.IsCompleted)
            {
                _delay = await _scheduled.Reader.ReadAsync(Ct);
            }
        }
    }

    private sealed class FixedStage(int stage) : IMigrationStageSource
    {
        public ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken) => ValueTask.FromResult<int?>(stage);
    }

    private sealed class Facts(bool releaseFloor = false) : IGroupFacts
    {
        public bool ReleaseFloorAttested => releaseFloor;

        public bool HasNoOrigin(string group, IReadOnlyList<string> cacheIds) => false;

        public bool TakesClaim(string group) => false;

        public string ClaimDescriptor(string group) => "rt";

        public TimeSpan? LongestSourceTtl(string group) => null;

        public bool AllAcceptRefill(string group, IReadOnlyList<string> cacheIds) => false;

        public IReadOnlyList<string> StartTaken(string group) => [];

        public bool ClaimWired(string group) => true;

        public bool FormatSettled(string group, StateRecord state) => true;

        public bool NodeListComplete(string group, StateRecord state) => true;

        public bool MemoryFits(string group, string reportId, long estimatedBytes, double headroom) => true;

        public bool InPlaceReadersDeployed(string group) => true;
    }

    private sealed class Space : IGroupKeySpace
    {
        public IReadOnlyList<string> Patterns(string group, MoveSide side) => [];

        public IReadOnlyList<string> PatternsOf(string group, string cacheId, MoveSide side) => [];

        public IReadOnlyList<string> OwnersOf(string group, MoveSide side, string key) => [];

        public bool HasValidDestination(string group, MoveSide side, string cacheId, string key) => true;

        public bool IsMovable(string group, MoveSide side, string cacheId, string key, TimeSpan? timeToLive) => false;

        public KeyMoveKind KindOf(string group, string cacheId) => KeyMoveKind.String;

        public TimeSpan? MaxSourceTtl(string group, string cacheId) => null;

        public bool HasHashCache(string group) => false;

        public string? CallerKeyOf(string group, MoveSide side, string cacheId, string key) => null;

        public TimeSpan MoveMinTtl(string group, string cacheId) => IGroupKeySpace.DefaultMoveMinTtl;
    }

    private sealed class NoClaim : IMoveClaim
    {
        public Task<ClaimOutcome> PendAsync(string group, string token, string descriptor, LeaseFence? fence, CancellationToken cancellationToken) => Task.FromResult(ClaimOutcome.Failed);

        public Task<ClaimOutcome> OpenAsync(string group, string token, bool restore, LeaseFence? fence, CancellationToken cancellationToken) => Task.FromResult(ClaimOutcome.Failed);

        public Task<ClaimOutcome> ReopenAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) => Task.FromResult(ClaimOutcome.Failed);

        public Task<ClaimOutcome> CertifyAsync(string group, string token, long ep, string terminal, LeaseFence? fence, CancellationToken cancellationToken) => Task.FromResult(ClaimOutcome.Failed);

        public Task<ClaimOutcome> ReleaseIdleAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) => Task.FromResult(ClaimOutcome.Failed);

        public Task<ClaimStatus> StatusAsync(string group, string token, CancellationToken cancellationToken) => Task.FromResult(ClaimStatus.Absent);
    }
}
