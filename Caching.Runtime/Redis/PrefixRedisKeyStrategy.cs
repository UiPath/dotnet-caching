namespace UiPath.Platform.Caching.Redis;

public class PrefixRedisKeyStrategy : IRedisKeyStrategy
{
    private readonly string _prefix;
    
    private readonly char _separator;

    public PrefixRedisKeyStrategy(string prefix, char separator)
    {
        _prefix = Guard.NotNullOrWhiteSpace(prefix, nameof(prefix)).ToLowerInvariant();
        _separator = char.ToLowerInvariant(Guard.NotWhiteSpace(separator, nameof(separator)));
    }

    public RedisKey GetRedisKey(CacheKey key) =>
        string.Join(_separator, _prefix, key);
}
