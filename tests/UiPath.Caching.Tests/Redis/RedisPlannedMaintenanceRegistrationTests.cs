using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Redis;

public class RedisPlannedMaintenanceRegistrationTests
{
    [Fact]
    public async Task Starting_planned_maintenance_opens_no_connection_of_its_own()
    {
        // A second multiplexer keeps every node it has learned, and only the connector's is rebuilt when one retires.
        var factory = Substitute.For<IConnectionMultiplexerFactory>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = "app",
            ["Caching:DefaultCache"] = KnownCacheProviderNames.Redis,
            ["Caching:Connections:Redis:ConnectionString"] = "localhost:6379",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(factory);
        services.AddCaching(configuration, b => b.AddRedisConnection().AddRedis());
        await using var provider = services.BuildServiceProvider();
        var maintenance = provider.GetRequiredService<RedisPlannedMaintenance>();

        await maintenance.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        factory.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void Planned_maintenance_depends_on_nothing_it_could_open_a_connection_with()
    {
        typeof(RedisPlannedMaintenance).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Should().BeEquivalentTo([typeof(ICachingTelemetryProvider), typeof(IRedisConnector), typeof(TimeProvider)]);
    }
}
