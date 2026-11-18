using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration;

/// <summary>The Redis key is the key text the router built from the application key map, untouched.</summary>
internal sealed class PassThroughKeyStrategyFactory : IRedisKeyStrategyFactory, IRedisKeyStrategy
{
    public static PassThroughKeyStrategyFactory Instance { get; } = new();

    public IRedisKeyStrategy Create(CacheOptions options, Type cacheType) => this;

    public IRedisKeyStrategy Create(CacheOptions options, string differentiator) => this;

    public RedisKey GetRedisKey(CacheKey key) => key.Name;
}
