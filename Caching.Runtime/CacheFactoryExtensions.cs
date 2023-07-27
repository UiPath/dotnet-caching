namespace UiPath.Platform.Caching;

public static class CacheFactoryExtensions
{
    public static ICache<T> CreateCache<T>(this ICacheFactory factory, string? providerName = null, Type? callerType = null) =>
        new Cache<T>(factory.CreateCache(providerName, typeof(T), callerType));

    public static IHashCache<T> CreateHashCache<T>(this ICacheFactory factory, string? providerName = null, Type? callerType = null) =>
        new HashCache<T>(factory.CreateHashCache(providerName, typeof(T), callerType));
}
