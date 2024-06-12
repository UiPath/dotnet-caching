namespace UiPath.Platform.Caching.Redis;

public interface IRedisKeyStrategyFactory
{
    IRedisKeyStrategy Create(CacheOptions options, Type cacheType);

    IRedisKeyStrategy Create(CacheOptions options, string differentiator);
}
