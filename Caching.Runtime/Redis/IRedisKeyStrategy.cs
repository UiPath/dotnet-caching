namespace UiPath.Platform.Caching.Redis;

public interface IRedisKeyStrategy
{
    RedisKey GetRedisKey(CacheKey key);
}
