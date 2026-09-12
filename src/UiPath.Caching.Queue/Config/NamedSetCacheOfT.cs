namespace UiPath.Caching.Queue.Config;

/// <inheritdoc cref="UiPath.Caching.Config.NamedCache{T}"/>
internal sealed class NamedSetCache<T>([ServiceKey] string name, IServiceProvider services)
    : SetCache<T>(
        services.GetRequiredKeyedService<ISetCache>(name),
        services.GetKeyedService<ICacheKeyStrategy>(name),
        services.GetKeyedService<ICachePolicyFactory>(name));
