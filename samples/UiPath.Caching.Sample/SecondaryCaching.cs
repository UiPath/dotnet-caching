using Microsoft.Extensions.Options;
using UiPath.Caching.CloudEvents;
using UiPath.Caching.Config;
using UiPath.Caching.Polly;
using UiPath.Caching.Queue.Config;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Sample;

/// <summary>A second caching stack on its own Redis server; only <c>Connections:SecondaryRedis</c> differs from the primary.</summary>
public static class SecondaryCaching
{
    public const string Key = "SecondaryRedis";

    public static IServiceCollection AddSecondaryCaching(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddNamedCaching(
                Key,
                configuration,
                b => b
                    .AddBroadcast()
                    .AddRedis()
                    .AddInMemoryRedis()
                    .AddMemory()
                    .AddQueueInMemoryRedis()
                    .AddResilienceStrategies()
                    .AddCloudEvents(),
                // The instrumentation itself stays on the application's container.
                configureServices: (child, root) => child.AddTransient<IConnectionMultiplexerFactory>(sp =>
                    new OpenTelemetryConnectionMultiplexerFactory(sp.GetRequiredService<IOptions<RedisConnectionOptions>>(), root)))
            .ExposeQueueCaches();

        return services;
    }
}
