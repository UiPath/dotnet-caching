namespace UiPath.Platform.Caching;

public static class QueueCacheFactoryExtensions
{
    // Mirrors CacheFactoryExtensions.CreateCache<T> (which wraps ICache in Cache<T>). No policy
    // factory is passed: the underlying RedisSetCache already applies the global default policy.
    public static ISetCache<T> CreateSetCache<T>(this IQueueCacheFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new SetCache<T>(factory.CreateSetCache());
    }
}
