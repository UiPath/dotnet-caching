using System.Globalization;

namespace UiPath.Caching.Redis;

public class ShardPrefixRedisKeyStrategy : PrefixRedisKeyStrategy
{
    private const string  ShardFormat = "{{{0}}}";
    public ShardPrefixRedisKeyStrategy(string prefix, char separator) : base(prefix, separator)
    {
    }

    // Wrapping a key that already carries a tag would hash the tag together with what precedes it.
    public override RedisKey GetRedisKey(CacheKey key) =>
        RedisHashTag.HasValidTag(key.Name)
            ? base.GetRedisKey(key)
            : string.Join(Separator, Prefix, string.Format(CultureInfo.InvariantCulture, ShardFormat, key));
}
