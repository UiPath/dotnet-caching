namespace UiPath.Platform.Caching.Redis;

public class RedisHashSetCache<T> : RegionCache<T>, IRedisRegionCache<T>
    where T : class
{
    public RedisHashSetCache(IRedisRegionCache redisRegionCache)
        : base(redisRegionCache)
    {
    }
}
