using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis.Profiling;
using UiPath.Caching.Config;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class InMemoryRedisMissIntegrationTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_single_caller_miss_reads_redis_once_and_writes_it_once()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var profiler = new CommandCountingProfiler();
        using var provider = Build(profiler);
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis);
        var key = Unique();
        (await cache.GetAsync<string>(Unique(), Ct)).Should().BeNull();
        _ = profiler.Session.FinishProfiling().ToList();

        (await cache.GetOrAddAsync<string>(key, static _ => Task.FromResult<string?>("generated"), TimeSpan.FromMinutes(5), Ct)).Should().Be("generated");

        var commands = profiler.Session.FinishProfiling().Select(c => c.Command).ToList();
        commands.Count(IsRead).Should().Be(1, "one read of the inner tier: a read-only script on Redis 7, a transaction before it");
        if (provider.GetRequiredService<IRedisConnector>().Version >= new Version(7, 0))
        {
            commands.Should().NotContain("EXEC", "a Redis 7 read is the script alone");
        }
        commands.Count(c => c.Contains("SET", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_callers_on_a_cold_key_read_redis_once_and_run_the_generator_once()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var profiler = new CommandCountingProfiler();
        using var provider = Build(profiler);
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.InMemoryRedis);
        var key = Unique();
        var calls = 0;
        async Task<string?> Load(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            return "loaded";
        }

        (await cache.GetAsync<string>(Unique(), Ct)).Should().BeNull();
        _ = profiler.Session.FinishProfiling().ToList();
        var callers = Enumerable.Range(0, 30).Select(_ => Task.Run(async () => await cache.GetOrAddAsync(key, Load, TimeSpan.FromMinutes(5), Ct), Ct)).ToArray();

        (await Task.WhenAll(callers)).Should().AllBe("loaded");
        var commands = profiler.Session.FinishProfiling().Select(c => c.Command).ToList();
        calls.Should().Be(1);
        // Each caller reads the inner tier once, and reads that overlap share one; a caller the pool starts after the first read ended reads again.
        commands.Count(IsRead).Should().BeInRange(1, callers.Length);
        commands.Count(c => c.Contains("SET", StringComparison.Ordinal)).Should().Be(1);
    }

    private static bool IsRead(string command) => command is "EXEC" or "EVAL_RO" or "EVALSHA_RO";

    private static string Unique() => $"it-{Guid.NewGuid():N}";

    private ServiceProvider Build(CommandCountingProfiler profiler) =>
        new ServiceCollection()
            .AddSingleton<IRedisProfiler>(profiler)
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o =>
                    {
                        o.ConnectionString = fixture.ConnectionString;
                        o.ProfilerEnabled = true;
                        o.ProfilingSessionFactory = () => profiler.Session;
                    });
                    b.AddRedis(o => o.ReadScriptsEnabled = true);
                    b.AddInMemoryRedis(_ => { });
                },
                o => o.AppShortName = "inmemoryredis")
            .BuildServiceProvider();

    private sealed class CommandCountingProfiler : IRedisProfiler
    {
        public ProfilingSession Session { get; } = new();

        public int Count => 0;

        public ProfilingSession? GetSession() => Session;

        public IDisposable CreateSession(string? sessionId) => NullRedisProfiler.Instance.CreateSession(sessionId);
    }
}
