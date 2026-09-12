namespace UiPath.Caching.Config;

/// <inheritdoc cref="NamedCache{T}"/>
internal sealed class NamedHashCache<T>([ServiceKey] string name, IServiceProvider services)
    : HashCache<T>(
        services.GetRequiredKeyedService<ICacheFactory>(name),
        services.GetKeyedService<ICacheKeyStrategy>(name),
        services.GetKeyedService<ICachePolicyFactory>(name));
