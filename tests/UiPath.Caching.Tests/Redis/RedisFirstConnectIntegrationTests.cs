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
            var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            var reads = Enumerable.Range(0, callers)
                .Select(_ => Task.Run(async () =>
                {
                    var read = cache.GetOrAddAsync<string>($"it-{Guid.NewGuid():N}", _ => Task.FromResult<string?>("generated"), TimeSpan.FromMinutes(1), Ct);
                    if (Interlocked.Increment(ref count) == callers)
                    {
                        returned.TrySetResult();
                    }
                    return await read;
                },
                Ct))
                .ToArray();
            // Every caller has started and is waiting on the connect, so none is queued for a thread: a caller blocked on the connect would hold its thread, and the rest would sit in the queue.
            await returned.Task.WaitAsync(Ct);
            reads.Should().OnlyContain(read => !read.IsCompleted);
            gated.Open();

            (await Task.WhenAll(reads)).Should().AllBe("generated");
        }
        finally
        {
            gated.Open();
        }
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
