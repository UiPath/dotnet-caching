namespace UiPath.Caching.Redis;

public class PrefixRedisKeyStrategy : IRedisKeyStrategy
{
    protected string Prefix { get; set; }
    protected char Separator { get; set; }

    public PrefixRedisKeyStrategy(string prefix, char separator)
    {
        Prefix = Guard.NotNullOrWhiteSpace(prefix, nameof(prefix)).ToLowerInvariant();
        Separator = char.ToLowerInvariant(Guard.NotWhiteSpace(separator, nameof(separator)));
    }

    public virtual RedisKey GetRedisKey(CacheKey key) =>
        string.Join(Separator, Prefix, key);
}
