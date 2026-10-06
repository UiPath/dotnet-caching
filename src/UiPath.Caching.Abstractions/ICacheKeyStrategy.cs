namespace UiPath.Caching;

public interface ICacheKeyStrategy
{
    CacheKey GetCacheKey<T>(CacheKey key);
}
