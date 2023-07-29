namespace UiPath.Platform.Caching;

public sealed class PrefixCacheKeyStrategy : ICacheKeyStrategy
{
    private readonly string _prefix;
    private readonly char _separator;

    public PrefixCacheKeyStrategy(string prefix, char? separator = null)
    {
        _prefix = Guard.NotNullOrWhiteSpace(prefix, nameof(prefix)).ToLowerInvariant();
        _separator = separator == null ? CacheOptions.KeySeparator :  char.ToLowerInvariant(Guard.NotWhiteSpace(separator.Value, nameof(separator)));
    }
    
    public CacheKey GetCacheKey<T>(CacheKey key) => string.Join(_separator, _prefix, key);
}
