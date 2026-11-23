using System.Text;
using UiPath.Caching.Config;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests;

// A key a service reaches through the router is the key it reaches through the library: the same bytes for every key strategy and every casing of the caller's key.
public sealed class RouterKeyIdentityTests : RouterTestBase
{
    public static TheoryData<string> Strategies => ["default", "default-shard", "prefix", "shard-prefix", "pass-through"];

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task The_router_reads_and_writes_the_bytes_the_library_does_for_the_same_cache_key(string strategyName)
    {
        var strategy = StrategyOf(strategyName);
        foreach (var casing in new[] { CacheKeyCasing.Insensitive, CacheKeyCasing.Sensitive, CacheKeyCasing.Verbatim })
        {
            foreach (var name in new[] { "Plain", "with space", "UPPER:Case:Key", "{brace}x", "ünï:cödé", "a/b/c", "12345", " padded " })
            {
                var store = new FakeMigrationCache();
                var node = NewNode(library: new LibraryPathClient<Item>(store, new FakeConnection(), n => Redis(strategy, n), TimeSpan.FromSeconds(1)), localCap: TimeSpan.Zero);
                ICache<Item> cache = node.Cache;
                var key = casing == CacheKeyCasing.Verbatim ? CacheKey.Verbatim(name) : new CacheKey(name, casing);

                await cache.GetAsync(key, Ct);
                await cache.SetAsync(key, new Item("v"), TimeSpan.FromMinutes(1), Ct);
                await cache.RemoveAsync(key, Ct);

                // The router stack writes the text it is given, and the library writes what the strategy makes of the key: both must be the same bytes.
                var pass = new PassThroughKeyStrategyFactory().Create(new CacheOptions(), RedisKeyspaces.String);
                var expected = strategyName == "pass-through" ? Encoding.UTF8.GetBytes(key.Name) : (byte[])strategy.GetRedisKey(key)!;
                store.Keys.Should().HaveCount(3);
                foreach (var routed in store.Keys)
                {
                    ((byte[])pass.GetRedisKey(routed)!).Should().Equal(expected, $"{strategyName} {casing} '{name}'");
                }
            }
        }
    }

    // What an app writes for a cache's RedisKey: the strategy applied to the name the router received, verbatim: the caller's key has already normalized it, and a trim or a lowercase here would part the router's key from the library's.
    private static string Redis(IRedisKeyStrategy strategy, string name) =>
        Encoding.UTF8.GetString((byte[])strategy.GetRedisKey(CacheKey.Verbatim(name))!);

    // The shard key is obsolete but still honored, and apps use it; it is set by name through reflection, so the test makes no obsolete call; a rename fails the lookup.
    private static CacheOptions ShardKeyOptions()
    {
        var options = new CacheOptions { AppShortName = "app", Separator = ':' };
        typeof(CacheOptions).GetProperty("ShardKeyEnabled")!.SetValue(options, true);
        return options;
    }

    private static IRedisKeyStrategy StrategyOf(string name) => name switch
    {
        "default" => new DefaultRedisKeyStrategyFactory().Create(new CacheOptions { AppShortName = "app", Separator = ':' }, RedisKeyspaces.String),
        "default-shard" => new DefaultRedisKeyStrategyFactory().Create(ShardKeyOptions(), RedisKeyspaces.String),
        "prefix" => new PrefixRedisKeyStrategy("tenant:cache", ':'),
        "shard-prefix" => new ShardPrefixRedisKeyStrategy("tenant:cache", ':'),
        _ => new PassThroughKeyStrategyFactory().Create(new CacheOptions(), RedisKeyspaces.String),
    };
}
