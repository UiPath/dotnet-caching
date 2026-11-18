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
        Action<IServiceCollection, IServiceProvider>? configureServices = null) =>
        AddNamedCaching(services, name, configuration, configure, sectionName, configureConnection, configureServices, sharePrimaryConnector: false);

    /// <summary>
    /// <see cref="AddNamedCaching(IServiceCollection, string, IConfiguration, Action{ICachingBuilder}, string, Action{RedisConnectionOptions}?, Action{IServiceCollection, IServiceProvider}?)"/>
    /// on the application's own <see cref="IRedisConnector"/>, which stays the application's: the stack opens no connection, exposes no connector, and runs no stream maintainer.
    /// It is a node of its own, with <c>?stack={name}</c> added to the source URI, so its Streams consumer group is its own; topic keys and cache names may then repeat the application's.
    /// </summary>
    public static INamedCaching AddNamedCachingOnPrimaryConnection(
        this IServiceCollection services,
        string name,
        IConfiguration configuration,
        Action<ICachingBuilder> configure,
        string sectionName = DefaultSectionName,
        Action<IServiceCollection, IServiceProvider>? configureServices = null) =>
        AddNamedCaching(services, name, configuration, configure, sectionName, configureConnection: null, configureServices, sharePrimaryConnector: true);

    private static INamedCaching AddNamedCaching(
        IServiceCollection services,
        string name,
        IConfiguration configuration,
        Action<ICachingBuilder> configure,
        string sectionName,
        Action<RedisConnectionOptions>? configureConnection,
        Action<IServiceCollection, IServiceProvider>? configureServices,
        bool sharePrimaryConnector)
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
        if (sharePrimaryConnector && ConfigurationExtensions.AsEnumerable(connection, makePathsRelative: true).Any(setting => setting.Value is not null))
        {
            throw new InvalidOperationException(
                $"AddNamedCachingOnPrimaryConnection('{name}') found settings under '{connection.Path}', which the stack would ignore: it uses the application's connector. " +
                "Remove them, or use AddNamedCaching for a connection of its own.");
        }

        if (!sharePrimaryConnector && configureConnection is null && string.IsNullOrWhiteSpace(connection[nameof(RedisConnectionOptions.ConnectionString)]))
        {
            throw new InvalidOperationException(
                $"AddNamedCaching('{name}') found no ConnectionString under '{connection.Path}'. A named caching stack needs its own " +
                $"connection there, or one from code through configureConnection; the keys it leaves unset are inherited from " +
                $"'{section.Path}:{NamedCachingContainer.ConnectionSection(NamedCachingContainer.PrimaryConnection)}'.");
        }

        services.AddKeyedSingleton(name, (root, key) =>
            NamedCachingContainer.Build((string)key!, root, section, configure, configureConnection, configureServices, sharePrimaryConnector));
        services.AddSingleton<IHostedService>(sp => new NamedCachingHostedService(name, sp));
        services.AddKeyedTransient(typeof(ICache<>), name, typeof(NamedCache<>));
        services.AddKeyedTransient(typeof(IHashCache<>), name, typeof(NamedHashCache<>));

        var stack = new NamedCaching(name, services)
            .Expose<ICacheFactory>()
            .Expose<ICacheKeyStrategy>()
            .Expose<ICachePolicyFactory>();

        // Exposed, the application's own connector would be disposed through the stack's name as well, which is not the stack's to offer.
        if (!sharePrimaryConnector)
        {
            stack.Expose<IRedisConnector>().Expose<IRedisPlannedMaintenance>();
        }

        return stack
            .Expose<IDistributedLock>()
            .Expose<ITopicFactory>()
            .Expose(child => child.GetRequiredService<ICacheFactory>().CreateCache())
            .Expose(child => child.GetRequiredService<ICacheFactory>().CreateHashCache());
    }
}
