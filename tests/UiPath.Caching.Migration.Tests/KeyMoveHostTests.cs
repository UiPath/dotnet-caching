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

    private readonly string _unique = Guid.NewGuid().ToString("N")[..10];
    private readonly System.Collections.Concurrent.ConcurrentQueue<KeyMoveAlert> _alerts = new();
    private ConnectionMultiplexer? _redis;
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

        (await UntilAsync(() => router.KeyMove!.Runtime is not null)).Should().BeTrue("the node starts with the library");
        (await UntilAsync(async () => (await Tokens(router, provider)).Any(t => t.StartsWith("snap=", StringComparison.Ordinal)))).Should().BeTrue("a listed group is read at startup, which gives the node a snapshot");
        var tokens = await Tokens(router, provider);

        tokens.Should().Contain("km").And.Contain($"kmg={Group}").And.Contain($"ca={Group}:advc");
        (await UntilAsync(async () => await Db.KeyExistsAsync(lead))).Should().BeTrue("a relevant group is led by whoever holds the lease");

        var init = await router.KeyMove!.Runtime!.Commands.InitAsync(Group, ["advc"], Ct);
        init.Code.Should().Be(MoveScripts.StateCreated);
        (await UntilAsync(async () => (await Tokens(router, provider)).Contains($"fl={Group}:2"))).Should().BeTrue("the stage flag is polled once the group is Init-ed and listed");

        await router.StopAsync(Ct);

        router.KeyMove.Runtime.Should().BeNull();
        (await UntilAsync(async () => !await Db.KeyExistsAsync(lead))).Should().BeTrue("a stopped node releases its lease");
    }

    [Fact]
    public async Task A_node_acknowledges_the_identity_of_an_Init_ed_group_it_acts_under()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(keyMove: true);
        var router = (Router)provider.GetRequiredService<IRouter>();
        (await UntilAsync(() => router.KeyMove!.Runtime is not null)).Should().BeTrue();

        await router.KeyMove!.Runtime!.Commands.InitAsync(Group, ["advc"], Ct);
        var state = (await router.KeyMove.Runtime.Store.ReadAsync(Group, Ct))!;

        (await UntilAsync(async () => (await Tokens(router, provider)).Any(t => t.StartsWith($"{Group}@{state.Ep}:{state.Id}:S0", StringComparison.Ordinal)))).Should().BeTrue();
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
        (await UntilAsync(() => router.KeyMove!.Runtime is not null)).Should().BeTrue();
        await router.KeyMove!.Runtime!.Commands.InitAsync(Group, ["advc"], Ct);
        var before = router.KeyMove.Runtime;
        var oldMux = connector.Database.Multiplexer;
        var lead = $"{Library}mg:{{{Group}}}:lead";
        (await UntilAsync(async () => await Db.KeyExistsAsync(lead))).Should().BeTrue();

        connector.ForceReconnect();
        (await UntilAsync(() => !ReferenceEquals(connector.Database.Multiplexer, oldMux))).Should().BeTrue("the connector replaced its multiplexer");

        (await UntilAsync(() => !ReferenceEquals(router.KeyMove.Runtime, before))).Should().BeTrue("the node rebuilt its services over the new connection");
        await Db.KeyDeleteAsync(lead);
        (await UntilAsync(async () => await Db.KeyExistsAsync(lead))).Should().BeTrue("the lease is taken again through the new connection");
        (await router.KeyMove.Runtime!.Store.ReadAsync(Group, Ct)).Should().NotBeNull("commands run over the new connection");

        // A snapshot loop that stopped at the reconnect would show an age that only grows, so the age must stay at or below one bucket well past 5 seconds.
        await Task.Delay(TimeSpan.FromSeconds(7), Ct);
        (await UntilAsync(async () => (await Tokens(router, provider)).Any(t => t is "snap=0" or "snap=5"))).Should().BeTrue("the snapshot loop reads through the new connection");
    }

    [Fact]
    public async Task A_lease_release_that_throws_does_not_keep_the_node_from_stopping_all_of_its_parts()
    {
        Assert.SkipUnless(fixture.Enabled, Skip);
        await using var provider = Provider(keyMove: true);
        var router = (Router)provider.GetRequiredService<IRouter>();
        var connector = provider.GetRequiredService<IRedisConnector>();
        (await UntilAsync(() => router.KeyMove!.Runtime is not null)).Should().BeTrue();

        // Every command of the node now fails, the lease release included.
        await connector.Database.Multiplexer.DisposeAsync();
        await router.KeyMove!.StopAsync();

        router.KeyMove.Runtime.Should().BeNull();
        await router.KeyMove.StopAsync();
    }

    private static async Task<bool> UntilAsync(Func<bool> condition) => await UntilAsync(() => Task.FromResult(condition()));

    private static async Task<bool> UntilAsync(Func<Task<bool>> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(25, Ct);
        }

        return false;
    }

    private static RedisKey AdvertisementKey(char separator) => RouterServiceCollectionExtensions.NodeListKey(
        new CacheOptions { Separator = separator },
        new RedisCacheOptions { RedisKeyStrategyFactory = PassThroughKeyStrategyFactory.Instance });

    private async Task<IReadOnlyList<string>> Tokens(Router router, ServiceProvider provider) => (await EntryAsync(router, provider)).Tokens;

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
        var store = AdvertisementStore(provider);
        _nodeKey = store.Key;
        _field = router.Advertiser!.Field;
        RouterAdvertisement? entry = null;
        await UntilAsync(async () =>
        {
            entry = (await store.ReadAsync(Ct)).Where(e => e.Field == _field).Select(e => e.Advertisement).FirstOrDefault();
            return entry is not null;
        });
        return entry ?? throw new InvalidOperationException("The router wrote no entry.");
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
        var services = new ServiceCollection().AddLogging();
        if (clock is not null)
        {
            services.AddSingleton<TimeProvider>(clock);
        }

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
                            LeaseRenewInterval = DriverTestClock.StartupInterval,
                            PollInterval = DriverTestClock.StartupInterval,
                        },
                        Alert = _alerts.Enqueue,
                        Driver = startable
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
