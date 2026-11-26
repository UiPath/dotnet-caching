using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using UiPath.Caching.Config;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>Runs a routed cache and its move controller on isolated Redis keys.</summary>
internal sealed class MovingCacheHost : IAsyncDisposable
{
    public const string Group = "group";
    public const string CacheId = "values";

    public static readonly TimeSpan MoverPassPause = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private readonly ConnectionMultiplexer _redis;
    private readonly string _name = $"move{Guid.NewGuid():N}";
    private readonly DateTimeOffset _dryRunAt;
    private readonly ConcurrentQueue<KeyMoveAlert> _alerts = new();
    private readonly LegacySource _legacy;
    private readonly KeyMoveValueClasses _values = new();
    private readonly RegisteredGroupFacts _facts;
    private readonly StageDriverOptions _driverOptions = new()
    {
        AckInterval = Poll,
        AckStable = Poll,
        FlagLowerHold = Poll,
        UpgradeHold = TimeSpan.Zero,
        CloseConfirm = TimeSpan.Zero,
        DrainedSoak = TimeSpan.Zero,
    };
    private readonly KeyMoveStateOptions _stateOptions;
    private readonly RegisteredKeySpace _space;
    private readonly RedisRouterMoveClaim _claim;
    private readonly SwitchableMoveRate _rate = new();
    private readonly SwitchableSchedulePoint _moverSchedule = new();
    private ServiceProvider? _services;
    private Router _router = null!;
    private int _stage;
    private string? _observedPass;

    private MovingCacheHost(ConnectionMultiplexer redis, ObservedClock clock, bool cluster, bool acceptRefill)
    {
        _redis = redis;
        Clock = clock;
        MoverClock = new ObservedClock(clock.GetUtcNow());
        RateClock = new ObservedClock(MoverClock.Manual);
        _dryRunAt = clock.GetUtcNow();
        _legacy = new LegacySource(redis, _name);
        _stateOptions = new KeyMoveStateOptions
        {
            ExpectedReplicasPerMaster = cluster ? null : 1,
            LeaseRenewInterval = Poll * 4,
            PollInterval = Poll,
            SlowPollInterval = Poll,
            ReplicaLagMax = Poll * 2,
            UpkeepInterval = Timeout.InfiniteTimeSpan,
        };
        Names = new MoveKeyNames(string.Empty, Prefix, cluster);
        var version = redis.GetServers().Where(server => !server.IsReplica).Min(server => server.Version)!;
        Scripts = new MoveScripts(Db, true, version);
        Topology = new MultiplexerTopology(redis, string.Empty, true, version, _stateOptions.ExpectedReplicasPerMaster, barrierWait: _stateOptions.BarrierWait);
        var cache = new KeyMoveCache(
            CacheId,
            Group,
            true,
            false,
            acceptRefill,
            null,
            Legacy("*"),
            Legacy,
            key => Parse(key, Legacy(string.Empty)),
            Native("*"),
            Native,
            key => key.EndsWith('}') ? Parse(key[..^1], Prefix + "native:{") : null);
        _space = new RegisteredKeySpace([cache], Prefix);
        Registration = cache;
        _facts = new RegisteredGroupFacts(
            [cache],
            true,
            new Dictionary<string, string> { [Group] = "format" },
            new Dictionary<string, string> { [Group] = "nodes" },
            new Dictionary<string, string> { [Group] = new DryRunStamp(_dryRunAt, 0, string.Empty).ReportId },
            claimWired: true,
            inPlaceReadersDeployed: new Dictionary<string, string> { [Group] = "in-place-readers" });
        _claim = new RedisRouterMoveClaim(new ClaimStore(Db, Names.Claim(_name), true, version), _facts);
    }

    public ObservedClock Clock { get; }

    public ObservedClock MoverClock { get; }

    public ObservedClock RateClock { get; }

    public IDatabase Db => _redis.GetDatabase();

    public MoveKeyNames Names { get; }

    public MultiplexerTopology Topology { get; }

    public ICache<List<long>> Cache => _router.Get<List<long>>(CacheId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Prefix => _name + ":";

    private KeyMoveCache Registration { get; }

    private MoveScripts Scripts { get; }

    private KeyMoveClient<List<long>> MoveClient => (KeyMoveClient<List<long>>)((RoutedCache<List<long>>)Cache).Library;

    private ICache Library => _services!.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);

    private KeyMoveNode Node => _router.KeyMove!;

    private KeyMoveRuntime Runtime => Node.Runtime!;

    private StageDriver Driver => Runtime.Driver!;

    private MoverHostedService Mover => _router.Library!.Started.OfType<MoverHostedService>().Single();

    public static async Task<MovingCacheHost> CreateAsync(RedisFixture fixture, bool cluster = false, bool acceptRefill = false)
    {
        var connection = cluster ? Environment.GetEnvironmentVariable("REDIS_CLUSTER_CONNECTION_STRING") : fixture.ConnectionString;
        var requireCluster = Environment.GetEnvironmentVariable("KEYMOVE_E2E_REQUIRE_CLUSTER") == "1";
        if (cluster)
        {
            const string missing = "Redis Cluster endpoint is unavailable. Set REDIS_CLUSTER_CONNECTION_STRING.";
            if (requireCluster)
            {
                Assert.False(string.IsNullOrWhiteSpace(connection), missing);
            }

            Assert.SkipWhen(string.IsNullOrWhiteSpace(connection), missing);
        }
        else
        {
            Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 and REDIS_CONNECTION_STRING to run.");
        }

        var redisOptions = ConfigurationOptions.Parse(connection!);
        redisOptions.AllowAdmin = true;
        redisOptions.AbortOnConnectFail = true;
        ConnectionMultiplexer redis;
        try
        {
            redis = await ConnectionMultiplexer.ConnectAsync(redisOptions);
        }
        catch (RedisConnectionException ex) when (cluster && !requireCluster)
        {
            Assert.Skip($"Redis Cluster is unreachable: {ex.Message}");
            throw;
        }

        var host = new MovingCacheHost(redis, new ObservedClock(await KeyMoveKit.ServerTimeAsync(redis.GetDatabase())), cluster, acceptRefill);
        try
        {
            await host.StartRouterAsync(redisOptions.ToString());
            return host;
        }
        catch
        {
            await host.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }
    }

    public static async Task AdvanceMoverPollAsync(ObservedClock clock)
    {
        var poll = MoverKit.Quick.PollInterval;
        while (true)
        {
            var (delay, due) = await clock.NextAlarmAsync(Ct);
            if (delay == poll && due > clock.GetTimestamp())
            {
                clock.Advance(poll);
                return;
            }
        }
    }

    public string Legacy(string key) => _legacy.RedisKey(key);

    public string Native(string key) => Prefix + "native:{" + key + "}";

    public async Task SeedAsync(string key, List<long> value, DateTimeOffset? deadline = null)
    {
        var expiry = deadline is { } at ? new Expiration(at.UtcDateTime) : Expiration.Default;
        Assert.True(await Db.StringSetAsync(Legacy(key), LegacyEnvelope.Write(value), expiry).WaitAsync(Ct));
    }

    public async Task<DateTimeOffset> ClientDeadlineAsync(DateTimeOffset deadline) =>
        Clock.ToDateTimeOffset(deadline - await KeyMoveKit.ServerTimeAsync(Db));

    public async Task PrepareMoveAsync()
    {
        var state = await StateAsync();
        var inventory = await Inventory().RunAsync(Group, Ct);
        var estimate = 1024L * inventory!.Movable;
        var dryRun = new DryRunStamp(_dryRunAt, estimate, "legacy keys scanned");
        await Scripts.FieldsAsync(Names.State(Group), state.Id, [(StateFields.DryRun, dryRun.Encode())], []).WaitAsync(Ct);
        await ApproveAsync(ApprovalKinds.Start);
    }

    public async Task SetStageAsync(int stage)
    {
        Volatile.Write(ref _stage, stage);
        (await Node.Stages.StepAsync(Group, Ct)).Should().BeTrue();
        await AdvertiseAsync();
    }

    public async Task<StateRecord> AdvanceAsync(string target, string direction = "fwd")
    {
        await ApplyStateAsync();
        var state = await StateAsync();
        StepOutcome? step = null;
        for (var i = 0; i < 5 && (state.State != target || state.Direction != direction || !state.Durable || (target == "S3" && state.AllAck != state.Id)); i++)
        {
            if (state is { State: "S3" } && target == "S4")
            {
                await WaitForMoverPassAsync(state.Direction);
            }

            step = await StepAsync();
            state = await StateAsync();
        }

        state.State.Should().Be(target, $"{step?.Plan} {step?.Result} {Diagnostics()}");
        state.Direction.Should().Be(direction);
        state.Durable.Should().BeTrue();
        if (target == "S3")
        {
            state.AllAck.Should().Be(state.Id);
        }

        MoveClient.PlanFor(write: false).Act?.Id.Should().Be(state.Id);
        Node.Monitor.AckedFor(Group)?.Id.Should().Be(state.Id);
        return state;
    }

    public async Task<StepOutcome?> StepAsync()
    {
        Clock.Advance(_stateOptions.ReplicaLagMax);
        (await Node.HoldLeaseAsync(Group)).Should().BeTrue();
        await ApplyStateAsync();
        var step = await Driver.StepAsync(Group, Ct);
        await ApplyStateAsync();
        return step;
    }

    public async Task WaitForPendingRulesAsync(string id)
    {
        await ApplyStateAsync();
        Node.Monitor.CurrentState(Group).Should().Match<StateRecord>(state => !state.Durable && state.Id == id);
        MoveClient.PlanFor(write: false).Rules.Reads.Should().Be(ReadRule.UnionNativeFirst);
    }

    public async Task WaitForMoverPassAsync(string direction = "fwd")
    {
        var state = await StateAsync();
        if (_observedPass != state.Id)
        {
            await AdvanceMoverPollAsync(Clock);
            var units = (await Node.ScanUnits!.ListAsync(Ct)).Count;
            for (var i = 0; i < units; i++)
            {
                await MoverClock.NextTimerAsync(delay => delay == MoverPassPause, Ct);
            }

            _observedPass = state.Id;
        }

        var passes = new MoverPasses(Node.ScanUnits!, Db, Names, Runtime.Store.ReadAsync);
        (await passes.FinishedAfterAckAsync(Group, direction, Ct)).Should().BeTrue();
    }

    public async Task ExpectAlertAsync(string code, string? gate = null, string? detail = null)
    {
        if (code == "flag_below_closed")
        {
            (await StepAsync())?.Plan.Kind.Should().Be(StepKind.None);
        }

        var step = await StepAsync() ?? throw new InvalidOperationException(Diagnostics());
        Assert.False(step.Plan.Kind is StepKind.Transit or StepKind.ColdStart, $"The driver advanced instead of reporting {code}.");
        step.Plan.AlertList.Should().Contain(alert => alert.Code == code
            && (detail == null || alert.Detail == detail)
            && (gate == null || alert.Detail.Split(',', StringSplitOptions.None).Any(entry => entry.StartsWith(gate + ":", StringComparison.Ordinal))),
            Diagnostics());
    }

    public async Task ExpectDrainBlockedAsync()
    {
        await ExpectAlertAsync("stage_pending");
        Clock.Advance(_stateOptions.ReplicaLagMax);
        await ExpectAlertAsync("stage_pending");
    }

    public Task UpkeepAsync() => Node.StepUpkeepAsync(Group, Ct);

    public void ExpireDrainInventory() => Clock.Advance(InventoryMoverStatus.ReuseFor);

    public async Task<StateRecord> StateAsync() =>
        await Runtime.Store.ReadAsync(Group, Ct) ?? throw new InvalidOperationException("The Redis state is absent.");

    public async Task ApproveAsync(string kind)
    {
        var confirmation = ApprovalKinds.ConfirmTexts.GetValueOrDefault(kind, "verified by the operator");
        (await Runtime.Commands.ApproveAsync(Group, kind, confirmation, "operator", Ct)).Should().Be(ApproveResult.Stored);
    }

    public async Task<TransitionOutcome> InterruptTransitionAsync(string next, int requestedStage)
    {
        await SetStageAsync(requestedStage);
        if (next == "S1")
        {
            _claim.InterruptOpen = true;
            try
            {
                return (await StepAsync())?.Result?.Outcome ?? throw new InvalidOperationException(Diagnostics());
            }
            finally
            {
                _claim.InterruptOpen = false;
            }
        }

        var state = await StateAsync();
        var advertiser = _router.Advertiser!;
        await ApplyStateAsync();
        var advert = new NodeAdvert(advertiser.Field, TimeSpan.Zero, advertiser.Current().Tokens);
        advert.Acknowledges(Group, state).Should().BeTrue();
        var passes = new MoverPasses(Node.ScanUnits!, Db, Names, Runtime.Store.ReadAsync);
        var mover = new InventoryMoverStatus(Inventory(), Clock, passes.FinishedAfterAckAsync);
        var input = new StepInput(
            Group,
            state,
            [advert],
            new FlagViews(_driverOptions, Clock).Sample(Group, [advert], StepPlanner.StageFlagOf(state)),
            _driverOptions.AckStable,
            Clock.GetUtcNow(),
            null,
            null,
            null,
            _stateOptions.StaleAfter(Poll),
            _facts,
            approved => new PreflightChecker(Topology, _stateOptions).CheckAsync(approved, Ct),
            () => Runtime.Data.CountNativeAsync(Group, Ct),
            () => mover.DrainAsync(Group, state.Direction, Ct),
            () => mover.ResidueInventoryAsync(Group, Ct),
            () => throw new InvalidOperationException("This test only interrupts forward transitions."));
        var plan = await StepPlanner.DecideAsync(input, _driverOptions);
        Assert.Equal(StepKind.Transit, plan.Kind);
        var spec = Assert.IsType<TransitionSpec>(plan.Spec);
        Assert.Equal(next, spec.State);

        var ep = state.Ep + 1;
        var id = MoveScripts.NewNonce();
        var (set, clear) = Generations.Of(spec.Binding, state, id);
        var target = new StateTarget(
            spec.State,
            spec.Direction,
            spec.NPop,
            ep,
            id,
            StateRecord.FormatPrev(state.State, state.Direction, state.NPop, state.Ep, state.Id),
            Clock.GetUtcNow().ToUnixTimeMilliseconds(),
            MoveId: spec.NewMoveId ? ep : null,
            Clear: clear,
            Set: set);
        var sequence = new TransitionSequence(Topology, Names, _stateOptions, time: Clock);
        var options = new SequenceOptions(BeforeCommit: _ => Task.FromResult<TransitionResult?>(new TransitionResult(TransitionOutcome.BarrierStuck)));
        return (await sequence.RunAsync(Group, StateExpectation.Holding(state.Ep, state.Id, 1), target, options, Ct)).Outcome;
    }

    // The router runs the mover; the host stops it once the router is up, and a test starts it with the rate and schedule it wants.
    public async Task StartMoverAsync(IKeyMoveHealthSource? health = null, ISchedulePoint? schedule = null)
    {
        var units = _services!.GetRequiredService<IScanUnits>();
        var options = new StaticOptionsMonitor<KeyMoveMoverOptions>(new KeyMoveMoverOptions
        {
            HealthTimeout = TimeSpan.FromSeconds(1),
            EvaluationInterval = TimeSpan.FromSeconds(1),
            StartKeysPerSecond = 1e12,
            MaxKeysPerSecond = 1e12,
            StartBytesPerSecond = 1e15,
            MaxBytesPerSecond = 1e15,
        });
        _rate.Inner = health is null
            ? AimdMoveRate.CreateDefault(units, options, RateClock)
            : new AimdMoveRate(health, RedisInfoWatch.Over(units, options, RateClock), options, RateClock);
        _moverSchedule.Inner = schedule ?? NoSchedulePoint.Instance;
        await Mover.StartAsync(Ct);
    }

    public Task StopMoverAsync() =>
        _router?.Library?.Started.OfType<MoverHostedService>().SingleOrDefault()?.StopAsync(CancellationToken.None) ?? Task.CompletedTask;

    public async Task<string[]> LegacyKeysAsync() => await KeysAsync(Legacy("*"));

    public async Task<string[]> NativeKeysAsync() => await KeysAsync(Prefix + "native:*");

    public Task AssertValueAsync(string key, List<long>? expected) => AssertValueCoreAsync(key, expected, Ct);

    public async Task AssertValueCoreAsync(string key, List<long>? expected, CancellationToken token)
    {
        var actual = await Cache.GetAsync(CacheKey.Verbatim(key), token);
        if (expected is null)
        {
            actual.Should().BeNull($"the acknowledged delete for {key} must be visible");
        }
        else
        {
            actual.Should().Equal(expected, $"the latest acknowledged value for {key} must be visible");
        }
    }

    public async Task<bool> SetAsync(string key, List<long> value, DateTimeOffset? deadline, CancellationToken token)
    {
        // The server checks marked-write deadlines against UTC; keep fake timers independent of that clock.
        var serverNow = await KeyMoveKit.ServerTimeAsync(Db);
        Clock.UtcOffset = serverNow - Clock.Manual.GetUtcNow();
        return deadline is { } at
            ? await Cache.SetAsync(CacheKey.Verbatim(key), value, at, token)
            : await Cache.SetAsync(CacheKey.Verbatim(key), value, token);
    }

    public async Task WriteAsync(string key, List<long> value) =>
        Assert.True(await SetAsync(key, value, null, Ct), $"The write for {key} was refused.");

    public async Task AssertNativeBytesMatchLibraryWriteAsync(string key, List<long> value, DateTimeOffset deadline)
    {
        var reference = Prefix + "reference:" + key;
        var expires = await ClientDeadlineAsync(deadline);
        Assert.True(await Library.SetAsync(CacheKey.Verbatim(reference), value, expires, policy: null, token: Ct));
        var bytesWrittenByLibrary = (byte[]?)(await Db.StringGetAsync(reference, CommandFlags.DemandMaster).WaitAsync(Ct));
        var bytesAfterMove = (byte[]?)(await Db.StringGetAsync(Native(key), CommandFlags.DemandMaster).WaitAsync(Ct));
        Assert.NotNull(bytesWrittenByLibrary);
        Assert.Equal(bytesWrittenByLibrary, bytesAfterMove);
        var referenceLife = Db.KeyTimeToLiveAsync(reference, CommandFlags.DemandMaster);
        var movedLife = Db.KeyTimeToLiveAsync(Native(key), CommandFlags.DemandMaster);
        await Task.WhenAll(referenceLife, movedLife).WaitAsync(Ct);
        (await referenceLife).Should().BeGreaterThan(TimeSpan.Zero);
        (await movedLife).Should().BeCloseTo((await referenceLife)!.Value, TimeSpan.FromMilliseconds(250), $"the TTL for {key} must be preserved");
    }

    public async Task AssertNativeLedgerAsync(IReadOnlyDictionary<string, List<long>?> ledger)
    {
        foreach (var (key, value) in ledger)
        {
            var bytes = (byte[]?)(await Db.StringGetAsync(Native(key), CommandFlags.DemandMaster).WaitAsync(Ct));
            if (value is null)
            {
                Assert.Null(bytes);
            }
            else
            {
                Assert.NotNull(bytes);
                var (_, nativeValue) = ((IByteCodecCache)Library).Decode<List<long>>(bytes, null);
                nativeValue.Should().Equal(value, $"Redis N must contain the last acknowledged value for {key}");
            }
        }

        (await NativeKeysAsync()).Should().BeEquivalentTo(ledger.Where(entry => entry.Value is not null).Select(entry => Native(entry.Key)));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopMoverAsync();
        }
        finally
        {
            try
            {
                if (_services is not null)
                {
                    await DisposeServicesAsync();
                }
            }
            finally
            {
                try
                {
                    foreach (var batch in (await KeysAsync($"*{_name}*")).Chunk(256))
                    {
                        await Task.WhenAll(batch.Select(key => Db.KeyDeleteAsync(key)));
                    }
                }
                finally
                {
                    await _redis.DisposeAsync();
                }
            }
        }
    }

    private static string? Parse(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : null;

    private async Task ApplyStateAsync()
    {
        await Node.Monitor.StepAsync(Group, Ct);
        new AckCoordinator(Node.Monitor, Node.Gate, _driverOptions, Clock).Tick(Group);
        // Expire the data path's cached rules after applying the snapshot.
        Clock.Advance(TimeSpan.FromMilliseconds(100));
        await AdvertiseAsync();
    }

    private async Task AdvertiseAsync() => (await _router.Advertiser!.WriteAsync(Ct)).Should().BeTrue();

    private async Task DisposeServicesAsync()
    {
        if (_router?.Find<List<long>>(CacheId) is { } cache)
        {
            await cache.StopRetriesAsync();
            await cache.Pending.FlushAsync();
        }

        await _services!.DisposeAsync();
    }

    private async Task StartRouterAsync(string connection)
    {
        var state = new KeyMoveSetup(
            [new GroupRegistration(Group, true, true, [CacheId])],
            new IntFlagStageSource((_, _, _) => ValueTask.FromResult(Volatile.Read(ref _stage))),
            _facts,
            _space,
            _claim,
            Prefix)
        {
            Caches = [Registration],
            Values = _values,
            State = _stateOptions,
            Driver = _driverOptions,
            Alert = _alerts.Enqueue,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = _name,
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = connection,
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["RouterCaching:AppShortName"] = _name,
            ["RouterCaching:SourceUri"] = "urn:" + _name,
            ["RouterCaching:Redis:FormatNegotiation:AdvertiseInterval"] = "00:00:00.100",
            ["RouterCaching:Redis:FormatNegotiation:LivenessWindow"] = "00:00:02",
            ["RouterCaching:Redis:FormatNegotiation:UpgradeHold"] = "00:00:03",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(Clock).AddSingleton<IMoveRate>(_rate);
        var adverts = Db.WithKeyPrefix(Prefix);
        services.AddCaching(configuration, builder => builder.AddRedisConnection().AddRedis(options =>
            options.RedisKeyStrategyFactory = PassThroughKeyStrategyFactory.Instance));
        services.AddCachingRouter(
            configuration,
            builder =>
            {
                builder.MoverOptions = MoverKit.Quick with { PassPause = MoverPassPause };
                builder.MoverSchedule = _moverSchedule;
                builder.MoverClock = MoverClock;
                builder
                .UseLibraryWhen(_ => true)
                .UsePinWhen(_ => true)
                .UseEnvelope(new LegacyEnvelopeProxy())
                .UseSourceConnection(() => adverts)
                .UseKeyMoveSetup(state)
                .Add(new RouterCacheRegistration<List<long>> { Name = CacheId, Group = Group, Source = _legacy, RedisKey = Legacy });
            },
            "RouterCaching");
        _services = services.BuildServiceProvider();
        _router = (Router)_services.GetRequiredService<IRouter>();

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMode(RouterMode mode, RouterModeReason reason)
        {
            if (mode == RouterMode.Library)
            {
                ready.TrySetResult();
            }
        }

        _router.ModeChanged += OnMode;
        try
        {
            if (_router.Mode != RouterMode.Library)
            {
                await ready.Task.WaitAsync(Ct);
            }
        }
        finally
        {
            _router.ModeChanged -= OnMode;
        }

        await Driver.StopAsync();
        await Node.Monitor.StopAsync();
        await Node.Stages.StopAsync();
        await StopMoverAsync();
        await Node.Monitor.StepAsync(Group, Ct);
        (await Runtime.Commands.InitAsync(Group, [CacheId], Ct)).Code.Should().Be(MoveScripts.StateCreated);
        await AdvanceAsync("S0", "none");
    }

    private async Task<string[]> KeysAsync(string pattern)
    {
        var results = new List<string>();
        foreach (var server in _redis.GetServers().Where(server => !server.IsReplica))
        {
            await foreach (var key in server.KeysAsync(Db.Database, pattern))
            {
                results.Add(key.ToString());
            }
        }

        return [.. results.Order(StringComparer.Ordinal)];
    }

    private KeyInventory Inventory()
    {
        var scanner = new MasterScanner(() => _redis.GetServers().Where(server => !server.IsReplica).ToArray(), Topology, Db.Database, string.Empty, Clock, Poll);
        return new KeyInventory(scanner, _space, Runtime.Store, Db, string.Empty, Clock, values: _values);
    }

    private string Diagnostics() => string.Join(" ", _alerts.TakeLast(8).Select(alert => $"{alert.Code}({alert.Detail})"));
}
