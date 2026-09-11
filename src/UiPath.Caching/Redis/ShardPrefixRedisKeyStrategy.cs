namespace UiPath.Caching.Redis;

public class ShardPrefixRedisKeyStrategy : PrefixRedisKeyStrategy
{
    public ShardPrefixRedisKeyStrategy(string prefix, char separator) : base(prefix, separator)
    {
    }

    public override RedisKey GetRedisKey(CacheKey key) =>
        string.Join(Separator, Prefix, RedisHashTag.EnsureTag(key.Name, nameof(ShardPrefixRedisKeyStrategy)));
}
