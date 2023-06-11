namespace UiPath.Platform.Caching;

public static class KeyResolverExtensions
{
    public static TopicKey GetTopicKey<T>(this IKeyResolver keyResolver, Type cacheType, string cacheName) =>
        keyResolver.GetTopicKey(typeof(T), cacheType, cacheName);

    public static CacheKey GetInternalCacheKey<T>(this IKeyResolver keyResolver, CacheKey originalCacheKey, Type cacheType, string cacheName) =>
        keyResolver.GetInternalCacheKey(originalCacheKey, typeof(T), cacheType, cacheName);
}
