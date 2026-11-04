#pragma warning disable CA2012 // NSubstitute setups call the ValueTask-returning members without consuming the result.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Redis;

// The StackExchange.Redis flag is process-wide, so no other test may connect while one of these has it on.
[CollectionDefinition(Name, DisableParallelization = true)]
public class DedicatedThreadsCollection
{
    public const string Name = "DedicatedThreads";
}

[Collection(DedicatedThreadsCollection.Name)]
public class RedisConnectionDedicatedThreadsTests
{
    [Fact]
    public void DedicatedThreads_defaults_to_false()
    {
        using var provider = Build();

        provider.GetRequiredService<IOptions<RedisConnectionOptions>>().Value.DedicatedThreads.Should().BeFalse();
    }

    [Fact]
    public void DedicatedThreads_binds_from_configuration()
    {
        using var provider = Build(("Caching:Connections:Redis:DedicatedThreads", "true"));

        provider.GetRequiredService<IOptions<RedisConnectionOptions>>().Value.DedicatedThreads.Should().BeTrue();
    }

    [Fact]
    public async Task Connecting_with_DedicatedThreads_sets_the_feature_flag()
    {
        ConnectionMultiplexer.GetFeatureFlag(RedisConnector.DedicatedThreadsFeatureFlag).Should().BeFalse("no other test leaves it on");
        try
        {
            using var connector = CreateConnector(dedicatedThreads: true);

            await connector.ConnectAsync(TestContext.Current.CancellationToken);

            ConnectionMultiplexer.GetFeatureFlag(RedisConnector.DedicatedThreadsFeatureFlag).Should().BeTrue();
        }
        finally
        {
            ConnectionMultiplexer.SetFeatureFlag(RedisConnector.DedicatedThreadsFeatureFlag, false);
        }
    }

    [Fact]
    public async Task Connecting_without_DedicatedThreads_leaves_the_feature_flag_off()
    {
        using var connector = CreateConnector(dedicatedThreads: false);

        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        ConnectionMultiplexer.GetFeatureFlag(RedisConnector.DedicatedThreadsFeatureFlag).Should().BeFalse();
    }

    private static RedisConnector CreateConnector(bool dedicatedThreads)
    {
        var options = Options.Create(new RedisConnectionOptions
        {
            ConnectionString = "127.0.0.1:6379",
            DedicatedThreads = dedicatedThreads,
            EnableHangDetection = false,
            EnableStaleEndpointDetection = false,
        });
        var factory = Substitute.For<IConnectionMultiplexerFactory>();
        factory.CreateAsync(Arg.Any<ConfigurationOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IConnectionMultiplexer>(Substitute.For<IConnectionMultiplexer>()));
        return new RedisConnector(
            Substitute.For<ICachingTelemetryProvider>(),
            new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options),
            factory,
            options);
    }

    private static ServiceProvider Build(params (string Path, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["Caching:AppShortName"] = "app",
            ["Caching:Connections:Redis:ConnectionString"] = "127.0.0.1:6379",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
        };
        foreach (var (path, value) in settings)
        {
            values[path] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration, b => b.AddRedisConnection());
        return services.BuildServiceProvider();
    }
}
