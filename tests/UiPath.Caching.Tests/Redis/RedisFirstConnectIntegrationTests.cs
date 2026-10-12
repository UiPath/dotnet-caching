using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class RedisFirstConnectIntegrationTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_first_calls_on_an_unconnected_connector_do_not_block_threads()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = fixture.ConnectionString, EnableHangDetection = false });
        var gated = new GatedFactory(new ConnectionMultiplexerFactory(options, NullRedisProfiler.Instance));
        using var connector = new RedisConnector(NullTelemetryProvider.Instance, new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options), gated, options);
        using var provider = BuildProvider(connector);
        var cache = provider.GetRequiredService<ICacheFactory>().CreateCache(KnownCacheProviderNames.Redis);

        try
        {
            // More concurrent callers than the pool has threads: each one that blocked on the connect would hold a thread until it finished.
            var callers = (Environment.ProcessorCount * 6) + 16;
            var reads = Enumerable.Range(0, callers)
                .Select(_ => Task.Run(async () => await cache.GetOrAddAsync<string>($"it-{Guid.NewGuid():N}", _ => Task.FromResult<string?>("generated"), TimeSpan.FromMinutes(1), Ct), Ct))
                .ToArray();
            // Every caller has started and is waiting on the connect, so none is queued for a thread: a caller blocked on the connect would hold its thread, and the rest would sit in the queue.
            var drained = await DrainedAsync(TimeSpan.FromSeconds(3));
            drained.Should().BeTrue("the pool has threads for every caller while all of them wait for the connect");
            gated.Open();

            (await Task.WhenAll(reads)).Should().AllBe("generated");
        }
        finally
        {
            gated.Open();
        }
    }

    private static async Task<bool> DrainedAsync(TimeSpan timeout)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            if (ThreadPool.PendingWorkItemCount == 0)
            {
                return true;
            }

            await Task.Delay(10, Ct);
        }

        return false;
    }

    private ServiceProvider BuildProvider(IRedisConnector connector) =>
        new ServiceCollection()
            .AddSingleton<IRedisConnector>(connector)
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(_ => { });
                },
                o => o.AppShortName = "redisonly")
            .BuildServiceProvider();

    private sealed class GatedFactory(IConnectionMultiplexerFactory inner) : IConnectionMultiplexerFactory
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _gate.TrySetResult();

        public async ValueTask<IConnectionMultiplexer> CreateAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
        {
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await inner.CreateAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
    }
}
