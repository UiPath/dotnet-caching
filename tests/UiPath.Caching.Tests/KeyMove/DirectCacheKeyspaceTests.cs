using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Redis;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The app uses some caches of the library directly. They are not routed and not registered, and the key move never touches their keys, in any key format.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class DirectCacheKeyspaceTests(RedisContainerFixture fixture) : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly List<ServiceProvider> _providers = [];
    private readonly string _app = "dc" + Guid.NewGuid().ToString("N")[..10];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_purge_and_the_inventory_leave_a_direct_cache_s_keys_alone_in_each_key_format(bool shardKeys)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var direct = Open(shardKeys);
        await direct.SetAsync("plain", "value-1", Ttl, Ct);
        await direct.SetAsync("other:key", "value-2", Ttl, Ct);
        var directKeys = await Redis(kit).ToArrayAsync(_app);
        directKeys.Should().NotBeEmpty($"the direct cache wrote its keys; all keys: {string.Join(' ', kit.Mux.GetServers().First(s => !s.IsReplica).Keys(pattern: $"*{_app}*").Select(k => (string)k!))}");

        var hashKey = await WriteDirectHashMarkerAsync(kit, shardKeys);

        var library = _app + ":";
        var routed = Routed(library, kit.Group);
        var space = new RegisteredKeySpace([routed], library, null, [$"{_app}:{RedisKeyspaces.String}:*", $"{_app}:{RedisKeyspaces.Hash}:*"]);
        var scanner = new MasterScanner(() => kit.Mux.GetServers(), kit.Topology, 0, string.Empty, TimeProvider.System, TimeSpan.Zero);
        var scripts = new MoveScripts(kit.ControlDb, true, kit.Mux.GetServers().Min(s => s.Version)!);
        var data = new GroupDataOps(scanner, space, kit.Store, kit.Scripts, scripts, kit.Names);
        var inventory = new KeyInventory(scanner, space, kit.Store, kit.Db, string.Empty, kit.Time);
        var init = await kit.InitAsync("a");
        (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, Ct)).Outcome.Should().Be(TransitionOutcome.Completed);
        var movingNative = $"{library}n:a:k1";
        var movingLegacy = $"{_app}:legacy:k2";
        await kit.Db.StringSetAsync(movingNative, "v");
        await kit.Db.StringSetAsync(movingLegacy, "v");
        try
        {
            (await data.CountNativeAsync(kit.Group, Ct)).Should().Be(2, "the moving cache's two keys; its broad predicate also accepts the direct ones, which only the exclusion keeps out");
            var state = (await kit.Store.ReadAsync(kit.Group, Ct))!;
            await data.PurgeNativeAsync(kit.Group, new DataGuard(state.Id, null), Ct);
            await data.PurgeLegacyAsync(kit.Group, new DataGuard(state.Id, null), Ct);
            await kit.Scripts.FieldsAsync(kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, state.Id)], []);
            var found = (await inventory.RunAsync(kit.Group, Ct))!;

            (await kit.Db.KeyExistsAsync(movingNative)).Should().BeFalse("the moving cache's own key is purged");
            (await kit.Db.KeyExistsAsync(movingLegacy)).Should().BeFalse();
            await AssertDirectKeysIntactAsync(kit, found, directKeys);

            (await kit.Db.KeyExistsAsync(hashKey)).Should().BeTrue();
            (await direct.GetAsync<string>("plain", Ct)).Should().Be("value-1");
            (await direct.GetAsync<string>("other:key", Ct)).Should().Be("value-2");
        }
        finally
        {
            await kit.Db.KeyDeleteAsync([.. directKeys.Select(k => (RedisKey)k), hashKey]);
        }
    }

    [Fact]
    public async Task Without_the_exclusion_a_greedy_registration_would_have_taken_the_direct_keys()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var direct = Open(shardKeys: false);
        await direct.SetAsync("plain", "value", Ttl, Ct);
        var library = _app + ":";
        var space = new RegisteredKeySpace([Routed(library, kit.Group)], library);

        var owners = space.OwnersOf(kit.Group, MoveSide.Native, (await Redis(kit).ToArrayAsync(_app))[0]);

        owners.Should().Equal(["a"], "the greedy predicate takes any key under the prefix, which only the exclusion prevents");
        await kit.Db.KeyDeleteAsync([.. (await Redis(kit).ToArrayAsync(_app)).Select(k => (RedisKey)k)]);
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }

    // A moving cache whose predicates take everything under the library prefix and the in-place namespace, and whose scans are broad: only the exclusion keeps a direct cache's keys out.
    private static async Task AssertDirectKeysIntactAsync(StateKit kit, InventoryResult found, string[] directKeys)
    {
        found.Residue["a"].Should().Be(0);
        found.Ambiguous.Should().Be(0);
        directKeys.Should().HaveCountGreaterThanOrEqualTo(2);
        foreach (var key in directKeys)
        {
            (await kit.Db.KeyExistsAsync(key)).Should().BeTrue($"the direct key {key} is untouched");
            (await kit.Db.KeyTimeToLiveAsync(key))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(4)).And.BeLessThanOrEqualTo(Ttl);
        }
    }

    private static KeyMoveCache Routed(string library, string group) =>
        new("a", group, false, false, false, null, library + "*", k => k, k => k, library + "*", k => library + "n:a:" + k, k => k, _ => true, _ => true);

    private static DirectKeys Redis(StateKit kit) => new(kit);

    private async Task<string> WriteDirectHashMarkerAsync(StateKit kit, bool shardKeys)
    {
        // The same strategies the library builds its keys with, for the keyspaces the direct caches use: the string and the hash cache of the app.
        var strategies = new DefaultRedisKeyStrategyFactory();
        var options = new CacheOptions { AppShortName = _app };
        typeof(CacheOptions).GetProperty("ShardKeyEnabled")!.SetValue(options, shardKeys);
        var hashKey = strategies.Create(options, RedisKeyspaces.Hash).GetRedisKey(new CacheKey("a-hash")).ToString();
        await kit.Db.StringSetAsync(hashKey, "hash-stand-in", Ttl);

        return hashKey;
    }

    private ICache Open(bool shardKeys)
    {
        var provider = new ServiceCollection()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis();
                },
                o =>
                {
                    o.AppShortName = _app;
                    typeof(CacheOptions).GetProperty("ShardKeyEnabled")!.SetValue(o, shardKeys);
                })
            .BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
    }

    private sealed class DirectKeys(StateKit kit)
    {
        public Task<string[]> ToArrayAsync(string app) => Task.FromResult(kit.Mux.GetServers().First(s => !s.IsReplica).Keys(pattern: $"{app}:s:*").Select(k => (string)k!).ToArray());
    }
}
