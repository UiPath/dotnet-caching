namespace UiPath.Caching;

public static class QueueCacheFactoryExtensions
{
    public static ISetCache<T> CreateSetCache<T>(this IQueueCacheFactory factory, string? providerName = null) =>
        new SetCache<T>(factory.CreateSetCache(providerName));
}
