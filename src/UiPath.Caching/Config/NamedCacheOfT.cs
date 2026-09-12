namespace UiPath.Caching.Config;

// Both optional, so this resolves what DI would inject into an unkeyed ICache<T> and falls back the same way.
internal sealed class NamedCache<T>([ServiceKey] string name, IServiceProvider services)
    : Cache<T>(
        services.GetRequiredKeyedService<ICacheFactory>(name),
        services.GetKeyedService<ICacheKeyStrategy>(name),
        services.GetKeyedService<ICachePolicyFactory>(name));
