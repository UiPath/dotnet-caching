using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class RedisConnectorIntegrationTests(RedisContainerFixture fixture)
{
    private RedisConnector NewConnector()
    {
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = fixture.ConnectionString, EnableHangDetection = false });
        var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options);
        var factory = new ConnectionMultiplexerFactory(options, NullRedisProfiler.Instance);
        return new RedisConnector(NullTelemetryProvider.Instance, optionsProvider, factory, options);
    }

    [Fact]
    public async Task Connects_And_RoundTrips()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");

        using var connector = NewConnector();
        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        await connector.Database.StringSetAsync("k", "v");
        var value = await connector.Database.StringGetAsync("k");

        value.ToString().Should().Be("v");
        connector.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task ForceReconnect_StaysConnected_AgainstLiveRedis()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");

        using var connector = NewConnector();
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        var reconnected = new TaskCompletionSource();
        connector.OnReconnected += (_, _) => reconnected.TrySetResult();

        connector.ForceReconnect();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        connector.IsConnected.Should().BeTrue();
        await connector.Database.StringSetAsync("k2", "v2");
        (await connector.Database.StringGetAsync("k2")).ToString().Should().Be("v2");
    }

    [Fact]
    public async Task GetMasterPhysicalConnectionMetrics_ReturnsData_OnLiveConnection()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");

        using var connector = NewConnector();
        await connector.ConnectAsync(TestContext.Current.CancellationToken);
        await connector.Database.PingAsync();

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        await multiplexer.GetDatabase().PingAsync();

        var metrics = connector.GetMasterPhysicalConnectionMetrics(multiplexer);

        metrics.Should().NotBeNull();
    }
}
