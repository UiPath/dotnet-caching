namespace UiPath.Platform.Caching.Redis;

public class RedisCache<T> : Cache<T>, IRedisCache<T>
    where T : class
{
    public RedisCache(IRedisCache cache)
        : base(cache)
    {
    }
}
