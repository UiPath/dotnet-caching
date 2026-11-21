using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Redis;

/// <summary>A miss that wins its NX fill on a real Redis allocates no more than the plain write it replaced did.</summary>
[Collection(RedisAllocationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SafeFillRedisAllocationTests(RedisContainerFixture fixture, ITestOutputHelper output) : IDisposable
{
    private const int Rounds = 7;
    private const int PerRound = 200;
    // What the same miss allocated before fills were NX, measured by this test on the commit they branched from (d793b2d1): 6562 on net8.0, 6540 on net10.0, scripts off.
    private const long LayeredMissBytes = 6562;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly string _app = $"sa{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_winning_fill_on_the_inner_tier_allocates_no_more_than_a_write()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Node(KnownCacheProviderNames.Redis);
        var set = await PerCallAsync(key => cache.SetAsync(key, "v", Ttl, Ct).AsTask());
        var fill = await PerCallAsync(key => cache.TryFillAsync(key, "v", Ttl, Ct).AsTask());

        output.WriteLine($"inner tier: SetAsync {set} B, TryFillAsync {fill} B");
        fill.Should().BeLessThanOrEqualTo(set, $"a write allocated {set} B and a winning fill {fill} B");
    }

    [Fact]
    public async Task A_layered_miss_that_wins_its_fill_allocates_within_its_budget()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Node(KnownCacheProviderNames.InMemoryRedis);
        var miss = await PerCallAsync(key => cache.GetOrAddAsync<string>(key, static _ => Task.FromResult<string?>("v"), Ttl, Ct).AsTask());

        output.WriteLine($"layered miss: {miss} B");
        miss.Should().BeLessThanOrEqualTo(LayeredMissBytes, $"a miss that generates and fills allocated {miss} B");
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static async Task<long> PerCallAsync(Func<CacheKey, Task> call)
    {
        var keys = Enumerable.Range(0, (Rounds + 1) * PerRound).Select(i => (CacheKey)$"alloc:{Guid.NewGuid():N}:{i}").ToArray();
        for (var i = 0; i < PerRound; i++)
        {
            await call(keys[i]);
        }

        // The fewest of several rounds: a call's continuations run on pool threads, where the runtime may allocate meanwhile.
        var fewest = long.MaxValue;
        for (var round = 1; round <= Rounds; round++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < PerRound; i++)
            {
                await call(keys[(round * PerRound) + i]);
            }

            fewest = Math.Min(fewest, (GC.GetTotalAllocatedBytes(precise: true) - before) / PerRound);
        }

        return fewest;
    }

    private ICache Node(string provider)
    {
        var node = new ServiceCollection()
            .AddLogging()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(o => o.ReadScriptsEnabled = false);
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
