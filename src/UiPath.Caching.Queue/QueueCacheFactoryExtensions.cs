namespace UiPath.Caching;

public static class QueueCacheFactoryExtensions
{
    // Mirrors CacheFactoryExtensions.CreateCache<T>. Split into two overloads (rather than one
    // optional parameter) because the parameterless one is the originally shipped surface.
    public static ISetCache<T> CreateSetCache<T>(this IQueueCacheFactory factory) =>
        new SetCache<T>(factory.CreateSetCache());

    public static ISetCache<T> CreateSetCache<T>(this IQueueCacheFactory factory, string? providerName) =>
        new SetCache<T>(factory.CreateSetCache(providerName));
}
