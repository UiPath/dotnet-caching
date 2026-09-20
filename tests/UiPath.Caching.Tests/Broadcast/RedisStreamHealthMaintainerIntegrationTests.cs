using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Redis;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Broadcast;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class RedisStreamHealthMaintainerIntegrationTests(RedisContainerFixture fixture)
{
    // A mocked IServer cannot model how StackExchange.Redis builds the message, which is where this failed.
    [Fact]
    public async Task A_stream_is_discovered_through_the_per_primary_scan()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");

        var cacheOptions = new CacheOptions { Enabled = true, AppShortName = $"it{Guid.NewGuid():N}"[..10] };
        var streamKey = new PrefixStrategy(RedisKeyspaces.Streams, cacheOptions).GetRedisKey("topic");
        var telemetry = new RecordingTelemetryProvider();

        using var connector = NewConnector();
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        await connector.Database.StreamAddAsync(streamKey, "field", "value");

        try
        {
            connector.GetPrimaries().Should().NotBeEmpty("the per-primary path is the one under test");

            var maintainer = new RedisStreamHealthMaintainer(
                connector,
                telemetry,
                Options.Create(new RedisStreamsTopicOptions { TrackStatistics = true, MaintainerEnabled = true }),
                Options.Create(new RedisCacheOptions { Enabled = true }),
                Options.Create(cacheOptions),
                NullLogger<RedisStreamHealthMaintainer>.Instance,
                TimeProvider.System);

            maintainer.Initialize();
            await maintainer.CheckStreamsAsync(TestContext.Current.CancellationToken);

            telemetry.Metrics
                .Where(metric => metric.Name == Metrics.Stream)
                .Should().Contain(metric => metric.Properties!["Name"] == streamKey.ToString());
        }
        finally
        {
            await connector.Database.KeyDeleteAsync(streamKey);
        }
    }

    private RedisConnector NewConnector()
    {
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = fixture.ConnectionString, EnableHangDetection = false });
        var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options);
        var factory = new ConnectionMultiplexerFactory(options, NullRedisProfiler.Instance);
        return new RedisConnector(NullTelemetryProvider.Instance, optionsProvider, factory, options);
    }
}
