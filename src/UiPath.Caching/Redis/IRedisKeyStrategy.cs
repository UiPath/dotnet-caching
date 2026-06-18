namespace UiPath.Caching.Redis;

public interface IRedisKeyStrategy
{
    RedisKey GetRedisKey(CacheKey key);
}
