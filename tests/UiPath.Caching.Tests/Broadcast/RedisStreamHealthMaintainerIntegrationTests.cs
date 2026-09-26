using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
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

    [Theory]
    [InlineData(false, "{{{0}}}:")]
    [InlineData(true, "{{{0}}}:")]
    public async Task A_stream_under_the_connector_prefix_is_discovered_when_KeyPrefix_is_not_set(bool decorated, string prefixFormat)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");

        // decorated hides the StackExchange.Redis wrapper from the reflection read, leaving only the probe.
        var cacheOptions = new CacheOptions { Enabled = true, AppShortName = $"it{Guid.NewGuid():N}"[..10] };
        var streamKey = new PrefixStrategy(RedisKeyspaces.Streams, cacheOptions).GetRedisKey("topic");
        var telemetry = new RecordingTelemetryProvider();

        using var inner = NewConnector();
        await inner.ConnectAsync(TestContext.Current.CancellationToken);
        using var connector = new PrefixingConnector(inner, string.Format(CultureInfo.InvariantCulture, prefixFormat, cacheOptions.AppShortName), decorated);
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

    /// <summary>A decorator of the kind an application wraps around its database, e.g. for telemetry.</summary>
    public class ForwardingDatabase : DispatchProxy
    {
        private IDatabase _inner = default!;

        public static IDatabase Wrap(IDatabase inner)
        {
            var proxy = Create<IDatabase, ForwardingDatabase>();
            ((ForwardingDatabase)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            try
            {
                return targetMethod!.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>What an application's own connector does: every command goes through a <c>WithKeyPrefix</c> database.</summary>
    private sealed class PrefixingConnector(RedisConnector inner, string prefix, bool decorated) : IRedisConnector
    {
        public event EventHandler? OnConnectionFailed
        {
            add => inner.OnConnectionFailed += value;
            remove => inner.OnConnectionFailed -= value;
        }

        public event EventHandler? OnConnectionRestored
        {
            add => inner.OnConnectionRestored += value;
            remove => inner.OnConnectionRestored -= value;
        }

        public event EventHandler? OnReconnected
        {
            add => inner.OnReconnected += value;
            remove => inner.OnReconnected -= value;
        }

        public bool IsConnected => inner.IsConnected;

        public Version Version => inner.Version;

        public IDatabase Database => decorated
            ? ForwardingDatabase.Wrap(inner.Database.WithKeyPrefix(prefix))
            : inner.Database.WithKeyPrefix(prefix);

        public ISubscriber Subscriber => inner.Subscriber;

        public void ForceReconnect() => inner.ForceReconnect();

        public EndPoint[] GetEndPoints(bool configuredOnly = false) => inner.GetEndPoints(configuredOnly);

        public IEnumerable<IServer> GetPrimaries() => inner.GetPrimaries();

        public void Dispose()
        {
        }
    }
}
