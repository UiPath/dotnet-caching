namespace UiPath.Caching;

public static class CacheFactoryExtensions
{
    public static ICache<T> CreateCache<T>(this ICacheFactory factory, string? providerName = null) =>
        new Cache<T>(factory.CreateCache(providerName));

    public static IHashCache<T> CreateHashCache<T>(this ICacheFactory factory, string? providerName = null) =>
        new HashCache<T>(factory.CreateHashCache(providerName));
}
