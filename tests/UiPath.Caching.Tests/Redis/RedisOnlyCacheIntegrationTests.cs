using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis.Profiling;
using UiPath.Caching.Config;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class RedisOnlyCacheIntegrationTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stateful_miss_stores_through_redis_and_a_second_call_is_a_hit()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var key = Unique();
        var calls = 0;

        var first = await cache.GetOrAddAsync<string, string>(key, "state", (s, _) => { calls++; return Task.FromResult<string?>(s + "!"); }, TimeSpan.FromMinutes(5), Ct);
        var second = await cache.GetOrAddAsync<string, string>(key, "other", static (_, _) => throw new InvalidOperationException("the generator ran on a hit"), TimeSpan.FromMinutes(5), Ct);
        var third = await cache.GetOrAddAsync<string, string>(key, "other", static (_, _) => throw new InvalidOperationException("the generator ran on a hit"), Ct);

        first.Should().Be("state!");
        second.Should().Be("state!");
        third.Should().Be("state!");
        calls.Should().Be(1);
        (await cache.TimeToLiveAsync<string>(key, Ct)).Should().BeGreaterThan(TimeSpan.FromMinutes(4));
    }

    [Fact]
    public async Task Concurrent_misses_on_one_key_run_the_generator_once()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var key = Unique();
        var calls = 0;

        async Task<string?> Load(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            return "loaded";
        }

        var reads = Enumerable.Range(0, 30).Select(_ => Task.Run(async () => await cache.GetOrAddAsync(key, Load, TimeSpan.FromMinutes(5), Ct), Ct)).ToArray();

        (await Task.WhenAll(reads)).Should().AllBe("loaded");
        calls.Should().Be(1);
        (await cache.GetAsync<string>(key, Ct)).Should().Be("loaded");
    }

    [Fact]
    public async Task Concurrent_callers_on_a_cold_key_send_one_get_and_one_set()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var profiler = new CommandCountingProfiler();
        using var provider = Build(profiler);
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var key = Unique();
        var calls = 0;

        async Task<string?> Load(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            return "loaded";
        }

        (await cache.GetAsync<string>(Unique(), Ct)).Should().BeNull();
        _ = profiler.Session.FinishProfiling().ToList();
        var callers = Enumerable.Range(0, 30).Select(_ => Task.Run(async () => await cache.GetOrAddAsync(key, Load, TimeSpan.FromMinutes(5), Ct), Ct)).ToArray();

        (await Task.WhenAll(callers)).Should().AllBe("loaded");
        var commands = profiler.Session.FinishProfiling().Where(c => c.GetStatement().Contains(key, StringComparison.Ordinal)).Select(c => c.Command).ToList();
        calls.Should().Be(1);
        commands.Count(c => c == "GET").Should().Be(1);
        commands.Count(c => c.Contains("SET", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Sequential_callers_on_a_cached_key_send_one_get_each()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var profiler = new CommandCountingProfiler();
        using var provider = Build(profiler);
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var key = Unique();
        await cache.SetAsync(key, "cached", TimeSpan.FromMinutes(5), Ct);
        _ = profiler.Session.FinishProfiling().ToList();

        for (var i = 0; i < 3; i++)
        {
            (await cache.GetOrAddAsync<string>(key, static _ => throw new InvalidOperationException("the generator ran on a hit"), TimeSpan.FromMinutes(5), Ct)).Should().Be("cached");
        }

        profiler.Session.FinishProfiling().Count(c => c.Command == "GET" && c.GetStatement().Contains(key, StringComparison.Ordinal)).Should().Be(3);
    }

    [Fact]
    public async Task A_generator_supplied_expiration_sets_the_ttl_redis_holds()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        using var provider = Build();
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);
        var live = Unique();
        var stale = Unique();

        (await cache.GetOrAddWithExpirationAsync<string>(live, _ => Task.FromResult(new GeneratedValue<string>("jwt", DateTimeOffset.UtcNow.AddMinutes(10))), Ct)).Should().Be("jwt");
        (await cache.GetOrAddWithExpirationAsync<string>(stale, _ => Task.FromResult(new GeneratedValue<string>("jwt", DateTimeOffset.UtcNow.AddMinutes(-1))), Ct)).Should().Be("jwt");

        (await cache.TimeToLiveAsync<string>(live, Ct)).Should().BeCloseTo(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
        (await cache.ContainsAsync<string>(stale, Ct)).Should().BeFalse("a value that has already expired is not stored");
    }

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private ServiceProvider Build(IRedisProfiler? profiler = null) =>
        new ServiceCollection()
            .AddSingleton(profiler ?? NullRedisProfiler.Instance)
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o =>
                    {
                        o.ConnectionString = fixture.ConnectionString;
                        if (profiler is CommandCountingProfiler counting)
                        {
                            o.ProfilerEnabled = true;
                            o.ProfilingSessionFactory = () => counting.Session;
                        }
                    });
                    b.AddRedis(_ => { });
                },
                o => o.AppShortName = "redisonly")
            .BuildServiceProvider();

    private sealed class CommandCountingProfiler : IRedisProfiler
    {
        public ProfilingSession Session { get; } = new();

        public int Count => 0;

        public ProfilingSession? GetSession() => Session;

        public IDisposable CreateSession(string? sessionId) => NullRedisProfiler.Instance.CreateSession(sessionId);
    }
}
