using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Redis;

/// <summary>Fail-safe on two nodes over one Redis with broadcast: the last value covers a failing generator once Redis lost the key, a peer's removal is never answered with it, and it never reaches Redis.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class FailSafeIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private static readonly TimeSpan LocalLifetime = TimeSpan.FromMilliseconds(400);
    private static readonly CachePolicy FailSafe = new() { FailSafeEnabled = true, LocalExpiration = LocalLifetime, DistributedExpiration = TimeSpan.FromMinutes(5), FailSafeCooldown = TimeSpan.Zero };

    private readonly string _app = $"fs{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_last_value_covers_a_failing_generator_and_never_reaches_Redis()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node("a");
        var reader = Node("b");
        var key = Unique();
        var shortLived = CachePolicyMerger.Merge(new CachePolicy { DistributedExpiration = TimeSpan.FromMilliseconds(500) }, FailSafe);
        (await node.GetOrAddAsync<string>(key, static _ => Task.FromResult<string?>("v1"), shortLived, Ct)).Should().Be("v1");
        await Task.Delay(TimeSpan.FromMilliseconds(800), Ct);

        (await node.GetOrAddAsync<string>(key, Throwing, shortLived, Ct)).Should().Be("v1");

        (await reader.GetAsync<string>(key, Ct)).Should().BeNull("a fallback is not written to Redis");
    }

    [Fact]
    public async Task A_peers_removal_over_the_stream_is_never_answered_with_the_older_value()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var a = Node("a");
        var b = Node("b");
        await WarmAsync(a, b);
        var key = Unique();
        (await a.GetOrAddAsync<string>(key, static _ => Task.FromResult<string?>("v1"), FailSafe, Ct)).Should().Be("v1");

        await b.RemoveAsync<string>(key, Ct);

        // Heard once a fresh generation no longer falls back; until then the removal is still on its way.
        (await EventuallyAsync(async () =>
        {
            try
            {
                await a.GetOrAddAsync<string>(key, Throwing, FailSafe, Ct);
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        })).Should().BeTrue("the peer's removal dropped the kept value");
    }

    [Fact]
    public async Task A_redis_only_cache_ignores_fail_safe()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var node = Node("a", KnownCacheProviderNames.Redis);
        var key = Unique();
        var shortLived = CachePolicyMerger.Merge(new CachePolicy { DistributedExpiration = TimeSpan.FromMilliseconds(500) }, FailSafe);
        (await node.GetOrAddAsync<string>(key, static _ => Task.FromResult<string?>("v1"), shortLived, Ct)).Should().Be("v1");
        (await node.GetOrAddAsync<string>(key, Throwing, shortLived, Ct)).Should().Be("v1", "a hit never runs the generator");
        await Task.Delay(TimeSpan.FromMilliseconds(800), Ct);

        var act = async () => await node.GetOrAddAsync<string>(key, Throwing, shortLived, Ct);
        await act.Should().ThrowAsync<InvalidOperationException>("the Redis-only provider keeps no fail-safe value");
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static Task<string?> Throwing(CancellationToken token) => Task.FromException<string?>(new InvalidOperationException("down"));

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    // The peer's subscription starts with its first read; a write before its consumer group exists is reported as a gap.
    private static async Task WarmAsync(ICache writer, ICache peer)
    {
        var key = Unique();
        (await peer.SetAsync(key, "old", Ct)).Should().BeTrue();
        (await writer.SetAsync(key, "new", Ct)).Should().BeTrue();
        (await EventuallyAsync(async () => await peer.GetAsync<string>(key, Ct) == "new")).Should().BeTrue("the peer's subscription to the topic is live");
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20, Ct);
        }

        return false;
    }

    private ICache Node(string source, string providerName = KnownCacheProviderNames.InMemoryRedis)
    {
        var provider = new ServiceCollection()
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
                    o.SourceUri = new Uri($"urn:{source}-{_app}");
                })
            .BuildServiceProvider();
        _nodes.Add(provider);
        return provider.GetRequiredService<ICacheFactory>().CreateCache(providerName);
    }
}
