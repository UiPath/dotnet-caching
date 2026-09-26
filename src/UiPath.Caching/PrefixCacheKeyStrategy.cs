namespace UiPath.Caching;

public sealed class PrefixCacheKeyStrategy : ICacheKeyStrategy
{
    private readonly string _prefixWithSeparator;

    public PrefixCacheKeyStrategy(string prefix, char? separator = null)
    {
        var lowered = Guard.NotNullOrWhiteSpace(prefix, nameof(prefix)).ToLowerInvariant();
        _prefixWithSeparator = lowered + (separator == null ? CacheOptions.KeySeparator : char.ToLowerInvariant(Guard.NotWhiteSpace(separator.Value, nameof(separator))));
    }

    public CacheKey GetCacheKey<T>(CacheKey key) =>
        key.WithName(_prefixWithSeparator + key.Name);

    public bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written)
    {
        written = _prefixWithSeparator.Length + key.Length;
        if (written > destination.Length)
        {
            written = 0;
            return false;
        }

        _prefixWithSeparator.CopyTo(destination);
        key.CopyTo(destination[_prefixWithSeparator.Length..]);
        return true;
    }
}
