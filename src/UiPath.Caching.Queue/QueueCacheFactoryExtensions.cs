namespace UiPath.Caching;

public static class QueueCacheFactoryExtensions
{
    // Mirrors CacheFactoryExtensions.CreateCache<T> (which wraps ICache in Cache<T>). No policy
    // factory is passed: the underlying set cache already applies the global default policy.
    public static ISetCache<T> CreateSetCache<T>(this IQueueCacheFactory factory, string? providerName = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        // Route the default case through the parameterless method so it stays equivalent to
        // CreateSetCache() (the provider-name overload is a default interface method).
        var setCache = providerName is null ? factory.CreateSetCache() : factory.CreateSetCache(providerName);
        return new SetCache<T>(setCache);
    }
}
