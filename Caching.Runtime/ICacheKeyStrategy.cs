namespace UiPath.Platform.Caching;

public interface ICacheKeyStrategy
{
    CacheKey GetCacheKey<T>(CacheKey key);
}
