using Microsoft.Extensions.Options;
using UiPath.Caching.Broadcast;
using UiPath.Caching.CloudEvents;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Polly;
using UiPath.Caching.Queue.Config;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Sample;

/// <summary>
/// A second, complete caching stack on its own Redis server. The library wires one Redis connection per
/// container, so the second stack is built in a child container that reads the same <c>Caching</c> section
/// with <c>Connections:Redis</c> replaced by <c>Connections:SecondaryRedis</c>. The pieces consumers resolve
/// are re-exposed as keyed services under <see cref="Key"/>.
/// </summary>
public static class SecondaryCaching
{
    public const string Key = "secondary";

    private const string PrimaryConnectionSection = "Caching:Connections:Redis";
    private const string SecondaryConnectionSection = "Caching:Connections:SecondaryRedis";

    public static IServiceCollection AddSecondaryCaching(this IServiceCollection services, IConfiguration configuration)
    {
        var childConfiguration = WithSecondaryConnection(configuration);

        services.AddKeyedSingleton<IServiceProvider>(Key, (root, _) => BuildChild(root, childConfiguration));

        services.AddKeyedSingleton<ICacheFactory>(Key, (sp, key) => Child(sp, key).GetRequiredService<ICacheFactory>());
        services.AddKeyedSingleton<IQueueCacheFactory>(Key, (sp, key) => Child(sp, key).GetRequiredService<IQueueCacheFactory>());
        services.AddKeyedSingleton<IDistributedLock>(Key, (sp, key) => Child(sp, key).GetRequiredService<IDistributedLock>());
        services.AddKeyedSingleton<ITopicFactory>(Key, (sp, key) => Child(sp, key).GetRequiredService<ITopicFactory>());
        services.AddKeyedSingleton<IRedisConnector>(Key, (sp, key) => Child(sp, key).GetRequiredService<IRedisConnector>());
        services.AddKeyedSingleton<ICache>(Key, (sp, key) => Child(sp, key).GetRequiredService<ICacheFactory>().CreateCache());
        services.AddKeyedSingleton<IHashCache>(Key, (sp, key) => Child(sp, key).GetRequiredService<ICacheFactory>().CreateHashCache());
        services.AddKeyedSingleton<ISetCache>(Key, (sp, key) => Child(sp, key).GetRequiredService<ISetCache>());

        // Connection warm-up, planned-maintenance tracking and the stream health maintainer are hosted services
        // in the child; the root host only starts what is registered with it.
        services.AddHostedService(sp => new ChildHostedServices(Child(sp, Key)));

        return services;
    }

    /// <summary>The application configuration with <c>Caching:Connections:Redis</c> overridden by every value under <c>Caching:Connections:SecondaryRedis</c>.</summary>
    private static IConfiguration WithSecondaryConnection(IConfiguration configuration)
    {
        var overrides = configuration.GetSection(SecondaryConnectionSection)
            .AsEnumerable(makePathsRelative: true)
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => $"{PrimaryConnectionSection}:{kv.Key}", kv => kv.Value);

        if (overrides.Count == 0)
        {
            throw new InvalidOperationException($"'{SecondaryConnectionSection}' is empty; the secondary caching stack needs at least a ConnectionString there.");
        }

        return new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(overrides)
            .Build();
    }

    private static IServiceProvider Child(IServiceProvider root, object? key) =>
        root.GetRequiredKeyedService<IServiceProvider>(key);

    private static ServiceProvider BuildChild(IServiceProvider root, IConfiguration configuration)
    {
        var child = new ServiceCollection();

        // Cross-cutting singletons come from the root so both stacks share one logger, one clock and one meter.
        child.AddSingleton(root.GetRequiredService<ILoggerFactory>());
        child.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        child.AddSingleton(root.GetRequiredService<TimeProvider>());
        child.AddSingleton(root.GetRequiredService<ICachingTelemetryProvider>());

        // Same OTel-instrumented multiplexer as the primary; the instrumentation itself is owned by the root.
        child.AddTransient<IConnectionMultiplexerFactory>(sp =>
            new OpenTelemetryConnectionMultiplexerFactory(sp.GetRequiredService<IOptions<RedisConnectionOptions>>(), root));

        // The same chain as Program.cs, over the same "Caching" section, minus the connection.
        child.AddCaching(configuration, b => b
            .AddRedisConnection()
            .AddBroadcast()
            .AddRedis()
            .AddInMemoryRedis()
            .AddMemory()
            .AddQueueInMemoryRedis()
            .AddResilienceStrategies()
            .AddCloudEvents());

        return child.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class ChildHostedServices(IServiceProvider child) : IHostedService
    {
        private readonly IHostedService[] _services = [.. child.GetServices<IHostedService>()];

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var service in _services)
            {
                await service.StartAsync(cancellationToken);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var service in _services.Reverse())
            {
                await service.StopAsync(cancellationToken);
            }
        }
    }
}
