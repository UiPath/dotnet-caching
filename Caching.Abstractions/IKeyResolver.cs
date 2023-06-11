using UiPath.Platform.Caching.Broadcast;

namespace UiPath.Platform.Caching;
public interface IKeyResolver
{
    TopicKey GetTopicKey(Type cacheEntryType, Type cacheType, string cacheName);

    CacheKey GetInternalCacheKey(CacheKey originalCacheKey, Type cacheEntryType, Type cacheType, string cacheName);

    string GetKey(string key, string? prefix = null);
}
