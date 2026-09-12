using Microsoft.Extensions.Hosting;
using UiPath.Caching.Locking;

namespace UiPath.Caching.Config;

public static class NamedCachingCollectionExtensions
{
    private const string DefaultSectionName = "Caching";

    /// <summary>
    /// A second caching stack on its own Redis connection, its caches, locks, topics and connector reached under
    /// <paramref name="name"/> as keyed services, anything else through <see cref="INamedCaching.Expose{TService}()"/>.
    /// It reads the same section as the application's caching, which must be registered too, and keeps its own L1
    /// caches; its connection is <c>Connections:{name}</c> laid over <c>Connections:Redis</c>, so unset keys are
    /// inherited. <paramref name="configure"/> is the usual chain without <c>AddRedisConnection</c>.
    /// </summary>
    /// <param name="configureConnection">Connection settings that need code, applied after both sections are bound.</param>
    /// <param name="configureServices">Registrations for the stack's container, made before the chain; the application's container is passed for what must be shared.</param>
    public static INamedCaching AddNamedCaching(
        this IServiceCollection services,
        string name,
        IConfiguration configuration,
        Action<ICachingBuilder> configure,
        string sectionName = DefaultSectionName,
        Action<RedisConnectionOptions>? configureConnection = null,
        Action<IServiceCollection, IServiceProvider>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);
        if (string.Equals(name, NamedCachingContainer.PrimaryConnection, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{name}' is the primary connection's name; a named caching stack needs another.", nameof(name));
        }

        if (services.Any(d => d.ServiceType == typeof(NamedCachingContainer) && Equals(d.ServiceKey, name)))
        {
            throw new InvalidOperationException($"AddNamedCaching('{name}') was already called on this service collection.");
        }

        var section = configuration.GetSection(sectionName);
        var connection = section.GetSection(NamedCachingContainer.ConnectionSection(name));

        // With none of its own it would inherit the primary's and land on that server; only code can supply one instead.
        if (configureConnection is null && string.IsNullOrWhiteSpace(connection[nameof(RedisConnectionOptions.ConnectionString)]))
        {
            throw new InvalidOperationException(
                $"AddNamedCaching('{name}') found no ConnectionString under '{connection.Path}'. A named caching stack needs its own " +
                $"connection there, or one from code through configureConnection; the keys it leaves unset are inherited from " +
                $"'{section.Path}:{NamedCachingContainer.ConnectionSection(NamedCachingContainer.PrimaryConnection)}'.");
        }

        services.AddKeyedSingleton(name, (root, key) =>
            NamedCachingContainer.Build((string)key!, root, section, configure, configureConnection, configureServices));
        services.AddSingleton<IHostedService>(sp => new NamedCachingHostedService(name, sp));
        services.AddKeyedTransient(typeof(ICache<>), name, typeof(NamedCache<>));
        services.AddKeyedTransient(typeof(IHashCache<>), name, typeof(NamedHashCache<>));

        return new NamedCaching(name, services)
            .Expose<ICacheFactory>()
            .Expose<ICacheKeyStrategy>()
            .Expose<ICachePolicyFactory>()
            .Expose<IRedisConnector>()
            .Expose<IRedisPlannedMaintenance>()
            .Expose<IDistributedLock>()
            .Expose<ILocalLock>()
            .Expose<ITopicFactory>()
            .Expose(child => child.GetRequiredService<ICacheFactory>().CreateCache())
            .Expose(child => child.GetRequiredService<ICacheFactory>().CreateHashCache());
    }
}
