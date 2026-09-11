using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Redis;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class RedisConnectorIntegrationTests(RedisContainerFixture fixture)
{

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
    public async Task RefreshClusterMembership_ReachesTheServer_UnderTheDefaultConnectionString()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        Assert.SkipWhen(ConfigurationOptions.Parse(fixture.ConnectionString).AllowAdmin, "The point is the default allowAdmin=false.");

        using var connector = NewConnector();
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        await using var observer = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString + ",allowAdmin=true");
        var clusterCallsBefore = await ClusterCommandCallsAsync(observer);

        var memberships = new List<ClusterMembership>();
        for (var refreshes = 0; refreshes < RedisConnector.NullTopologyRefreshLimit; refreshes++)
        {
            memberships.Add(await connector.RefreshClusterMembershipAsync(multiplexer));
        }

        memberships.Take(RedisConnector.NullTopologyRefreshLimit - 1).Should().AllSatisfy(m => m.Conclusive.Should().BeFalse("one missing configuration could still be a lost reply"));
        memberships[^1].Conclusive.Should().BeTrue();
        memberships[^1].Members.Should().BeNull("a standalone server has no cluster configuration");
        (await ClusterCommandCallsAsync(observer) - clusterCallsBefore).Should().BeGreaterThanOrEqualTo(
            RedisConnector.NullTopologyRefreshLimit, "each refresh must carry the client's own CLUSTER NODES to the server");
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
        metrics!.EndPoint.Should().BeOneOf(multiplexer.GetEndPoints());
        metrics.AwaitingResponseCount.Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>Server-side count of CLUSTER commands, which the client only ever sends from its handshake.</summary>
    private static async Task<long> ClusterCommandCallsAsync(ConnectionMultiplexer observer)
    {
        var sections = await observer.GetServer(observer.GetEndPoints()[0]).InfoAsync("commandstats");
        return sections.SelectMany(section => section)
            .Where(stat => stat.Key.StartsWith("cmdstat_cluster", StringComparison.OrdinalIgnoreCase))
            .Sum(stat => long.Parse(stat.Value.Split(',')[0]["calls=".Length..], CultureInfo.InvariantCulture));
    }
    private RedisConnector NewConnector()
    {
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = fixture.ConnectionString, EnableHangDetection = false });
        var optionsProvider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options);
        var factory = new ConnectionMultiplexerFactory(options, NullRedisProfiler.Instance);
        return new RedisConnector(NullTelemetryProvider.Instance, optionsProvider, factory, options);
    }
}
