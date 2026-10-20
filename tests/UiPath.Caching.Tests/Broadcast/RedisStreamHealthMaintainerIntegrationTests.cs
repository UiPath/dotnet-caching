using System.Net;
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

    // Proves the fix only against a cluster connection string: on a standalone server the routed SCAN already reaches every key.
    [Fact]
    public async Task Every_stream_is_discovered_through_a_connector_that_does_not_implement_GetPrimaries()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");

        var cacheOptions = new CacheOptions { Enabled = true, AppShortName = $"it{Guid.NewGuid():N}"[..10] };
        var keyStrategy = new PrefixStrategy(RedisKeyspaces.Streams, cacheOptions);
        var streamKeys = Enumerable.Range(0, 12).Select(i => keyStrategy.GetRedisKey($"topic{i}")).ToArray();
        var telemetry = new RecordingTelemetryProvider();

        using var inner = NewConnector();
        await inner.ConnectAsync(TestContext.Current.CancellationToken);
        using var connector = new ForwardingConnector(inner);
        foreach (var streamKey in streamKeys)
        {
            await connector.Database.StreamAddAsync(streamKey, "field", "value");
        }

        try
        {
            ((IRedisConnector)connector).GetPrimaries().Should().BeEmpty("the multiplexer fallback is the path under test");

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
                .Select(metric => metric.Properties!["Name"])
                .Should().BeEquivalentTo(streamKeys.Select(key => key.ToString()));
        }
        finally
        {
            foreach (var streamKey in streamKeys)
            {
                await connector.Database.KeyDeleteAsync(streamKey);
            }
        }
    }

    // The routed SCAN ran in the connector's database; a per-primary SCAN must not fall back to the default one.
    [Fact]
    public async Task A_stream_in_a_non_default_database_is_discovered_through_a_connector_that_does_not_implement_GetPrimaries()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");

        var cacheOptions = new CacheOptions { Enabled = true, AppShortName = $"it{Guid.NewGuid():N}"[..10] };
        var streamKey = new PrefixStrategy(RedisKeyspaces.Streams, cacheOptions).GetRedisKey("topic");
        var telemetry = new RecordingTelemetryProvider();

        using var inner = NewConnector();
        await inner.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.SkipWhen(
            inner.Database.Multiplexer.GetServers().Any(server => server.ServerType == ServerType.Cluster),
            "A cluster has only database 0.");
        using var connector = new ForwardingConnector(inner, database: 3);
        await connector.Database.StreamAddAsync(streamKey, "field", "value");

        try
        {
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

    // Stands in for a consumer's own connector, which inherits GetPrimaries' empty default.
    private sealed class ForwardingConnector(RedisConnector inner, int? database = null) : IRedisConnector
    {
        public event EventHandler? OnConnectionFailed { add => inner.OnConnectionFailed += value; remove => inner.OnConnectionFailed -= value; }

        public event EventHandler? OnConnectionRestored { add => inner.OnConnectionRestored += value; remove => inner.OnConnectionRestored -= value; }

        public event EventHandler? OnReconnected { add => inner.OnReconnected += value; remove => inner.OnReconnected -= value; }

        public bool IsConnected => inner.IsConnected;

        public Version Version => inner.Version;

        public IDatabase Database => database is { } db ? inner.Database.Multiplexer.GetDatabase(db) : inner.Database;

        public ISubscriber Subscriber => inner.Subscriber;

        public void ForceReconnect() => inner.ForceReconnect();

        public EndPoint[] GetEndPoints(bool configuredOnly = false) => inner.GetEndPoints(configuredOnly);

        public void Dispose()
        {
        }
    }
}
