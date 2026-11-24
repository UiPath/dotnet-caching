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

    private readonly string _unique = Guid.NewGuid().ToString("N")[..10];
    private ConnectionMultiplexer? _redis;
    private RedisKey? _nodeKey;
    private string? _field;

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
        if (_redis is not null)
        {
            await Db.KeyDeleteAsync([$"{Library}mg:{{{Group}}}:state", $"{Library}mg:{{{Group}}}:lead"]);
            if (_nodeKey is { } key && _field is not null)
            {
                await Db.HashDeleteAsync(key, _field);
            }

            await _redis.DisposeAsync();
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

    private static async Task<bool> UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
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

    private async Task<IReadOnlyList<string>> Tokens(Router router, ServiceProvider provider) => (await EntryAsync(router, provider)).Tokens;

    private async Task<RouterAdvertisement> EntryAsync(Router router, ServiceProvider provider)
    {
        var cacheOptions = provider.GetRequiredKeyedService<IOptions<CacheOptions>>(RouterServiceCollectionExtensions.StackName).Value;
        var redisOptions = provider.GetRequiredKeyedService<IOptions<RedisCacheOptions>>(RouterServiceCollectionExtensions.StackName).Value;
        _nodeKey = RouterServiceCollectionExtensions.NodeListKey(cacheOptions, redisOptions);
        _field = router.Advertiser!.Field;
        var store = new RouterAdvertisementStore(_ => ValueTask.FromResult(Db), _nodeKey.Value);
        RouterAdvertisement? entry = null;
        await UntilAsync(async () =>
        {
            entry = (await store.ReadAsync(Ct)).Where(e => e.Field == _field).Select(e => e.Advertisement).FirstOrDefault();
            return entry is not null;
        });
        return entry ?? throw new InvalidOperationException("The router wrote no entry.");
    }

    private ServiceProvider Provider(bool keyMove, IReadOnlyList<GroupRegistration>? groups = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = "kmhost",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = fixture.ConnectionString,
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["RouterCaching:AppShortName"] = "kmhostrouter",
            ["RouterCaching:Separator"] = "$",
            ["RouterCaching:SourceUri"] = "urn:km-host",
            ["RouterCaching:Redis:FormatNegotiation:LivenessWindow"] = "00:00:30",
            ["RouterCaching:Redis:FormatNegotiation:AdvertiseInterval"] = "00:00:01",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var source = new LegacySource(_redis!, "advc");
        var services = new ServiceCollection().AddLogging();
        services.AddCaching(configuration, builder => builder.AddRedisConnection());
        services.AddCachingRouter(
            configuration,
            router =>
            {
                router
                    .UseLibraryWhen(_ => true)
                    .UseEnvelope(new LegacyEnvelopeProxy())
                    .UseNodeIdentity("kmh" + _unique)
                    .UseSourceConnection(() => Db)
                    .Add(new RouterCacheRegistration<List<long>> { Name = "advc", Group = Group, Source = source, RedisKey = source.RedisKey });
                if (keyMove)
                {
                    router.UseKeyMoveSetup(new KeyMoveSetup(groups ?? [new GroupRegistration(Group, false, true, ["advc"])], new FixedStage(2), new Facts(), new Space(), new NoClaim(), Library)
                    {
                        State = new KeyMoveStateOptions { ExpectedReplicasPerMaster = 0, LeaseRenewInterval = TimeSpan.FromMilliseconds(200), PollInterval = TimeSpan.FromMilliseconds(200) },
                        Driver = new StageDriverOptions { AckInterval = TimeSpan.FromMilliseconds(50) },
                    });
                }
            },
            "RouterCaching");
        return services.BuildServiceProvider();
    }

    private sealed class FixedStage(int stage) : IMigrationStageSource
    {
        public ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken) => ValueTask.FromResult<int?>(stage);
    }

    private sealed class Facts : IGroupFacts
    {
        public bool ReleaseFloorAttested => false;

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
