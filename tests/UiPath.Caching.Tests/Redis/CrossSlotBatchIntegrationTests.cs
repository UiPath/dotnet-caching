using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Redis;

/// <summary>The multi-key calls over keys of many slots; on a cluster each slot is its own command, on a standalone server one.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class CrossSlotBatchIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private readonly string _app = $"cs{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(KnownCacheProviderNames.Redis)]
    [InlineData(KnownCacheProviderNames.InMemoryRedis)]
    public async Task A_batch_set_get_and_remove_over_many_slots_reach_every_key(string provider)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(provider);
        var keys = Keys(40);
        var pairs = keys.Select(k => new KeyValuePair<CacheKey, string?>(k, $"v-{k.Name}")).ToArray();

        (await cache.SetAsync(pairs, TimeSpan.FromMinutes(5), null, Ct)).Should().BeTrue();

        (await cache.GetAsync<string>(keys, null, Ct)).Should().Equal(pairs);
        var entries = await cache.GetCacheEntriesAsync<string>(keys, null, Ct);
        entries.Select(e => (e.Key, e.Value.Value)).Should().Equal(pairs.Select(p => (p.Key, p.Value)));
        entries.Should().AllSatisfy(e => e.Value.Expiration.Should().BeAfter(DateTimeOffset.UtcNow));

        (await cache.RemoveAsync<string>(keys, Ct)).Should().BeTrue();

        (await Cache(KnownCacheProviderNames.Redis).GetAsync<string>(keys, null, Ct)).Should().OnlyContain(p => p.Value == null);
    }

    [Theory]
    [InlineData(KnownCacheProviderNames.Redis)]
    [InlineData(KnownCacheProviderNames.InMemoryRedis)]
    public async Task A_batch_get_or_add_over_many_slots_generates_the_misses_once_and_stores_them(string provider)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(provider);
        var keys = Keys(40);
        await cache.SetAsync(keys[0], "cached", TimeSpan.FromMinutes(5), null, Ct);
        var entries = keys.Select(k => new KeyValuePair<CacheKey, string>(k, k.Name)).ToArray();
        var generated = new List<string>();

        var result = await cache.GetOrAddAsync<string, string>(
            entries,
            (states, _) =>
            {
                generated.AddRange(states);
                return Task.FromResult(states.Select(s => new KeyValuePair<string, string?>(s, $"g-{s}")).ToArray());
            },
            TimeSpan.FromMinutes(5),
            null,
            Ct);

        result.Should().Equal(entries.Select((e, i) => new KeyValuePair<string, string?>(e.Value, i == 0 ? "cached" : $"g-{e.Value}")));
        generated.Should().BeEquivalentTo(keys.Skip(1).Select(k => k.Name));
        (await Cache(KnownCacheProviderNames.Redis).GetAsync<string>(keys.Skip(1).ToArray(), null, Ct))
            .Select(p => p.Value).Should().Equal(keys.Skip(1).Select(k => $"g-{k.Name}"));
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static CacheKey[] Keys(int count) => [.. Enumerable.Range(0, count).Select(i => (CacheKey)$"key-{i}-{Guid.NewGuid():N}")];

    private ICache Cache(string provider)
    {
        var node = new ServiceCollection()
            .AddLogging()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis();
                    b.AddBroadcast();
                    b.AddInMemoryRedis(o => o.LocalMaxExpiration = TimeSpan.FromMinutes(1));
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.SourceUri = new Uri($"urn:{_app}-{_nodes.Count}");
                })
            .BuildServiceProvider();
        _nodes.Add(node);
        return node.GetRequiredService<ICacheFactory>().CreateCache(provider);
    }
}
