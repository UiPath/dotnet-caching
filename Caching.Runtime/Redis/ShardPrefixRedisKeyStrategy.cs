using System.Globalization;

namespace UiPath.Platform.Caching.Redis;

public class ShardPrefixRedisKeyStrategy : PrefixRedisKeyStrategy
{
    private const string  ShardFormat = "{{{0}}}";
    public ShardPrefixRedisKeyStrategy(string prefix, char separator) : base(prefix, separator)
    {
    }

    public override RedisKey GetRedisKey(CacheKey key) =>
        string.Join(Separator, Prefix, string.Format(CultureInfo.InvariantCulture, ShardFormat, key));
}
