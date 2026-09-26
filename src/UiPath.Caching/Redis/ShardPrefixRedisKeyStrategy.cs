namespace UiPath.Caching.Redis;

public class ShardPrefixRedisKeyStrategy : PrefixRedisKeyStrategy
{
    public ShardPrefixRedisKeyStrategy(string prefix, char separator) : base(prefix, separator)
    {
    }

    public override RedisKey GetRedisKey(CacheKey key) =>
        ((RedisKey)RedisHashTag.EnsureTag(key.Name, nameof(ShardPrefixRedisKeyStrategy))).Prepend(KeyPrefix);
}
