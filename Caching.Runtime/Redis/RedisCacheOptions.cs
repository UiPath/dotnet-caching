namespace UiPath.Platform.Caching.Redis;

public class RedisCacheOptions : CacheOptionsBase
{
    public int Version { get; set; } = 6;

    public IRedisKeyStrategyFactory? RedisKeyStrategyFactory { get; set; }
}
