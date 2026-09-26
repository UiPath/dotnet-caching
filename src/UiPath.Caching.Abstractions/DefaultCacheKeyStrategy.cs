namespace UiPath.Caching;

public sealed class DefaultCacheKeyStrategy : ICacheKeyStrategy
{
    public CacheKey GetCacheKey<T>(CacheKey key) => key;

    public bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written)
    {
        if (!key.TryCopyTo(destination))
        {
            written = 0;
            return false;
        }

        written = key.Length;
        return true;
    }
}
