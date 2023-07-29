namespace UiPath.Platform.Caching;

public sealed class DefaultCacheKeyStrategy : ICacheKeyStrategy
{
    public CacheKey GetCacheKey<T>(CacheKey key) => key;
}
