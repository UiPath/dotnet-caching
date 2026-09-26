namespace UiPath.Caching;

public interface ICacheKeyStrategy
{
    CacheKey GetCacheKey<T>(CacheKey key);

    /// <summary>Writes the text <see cref="GetCacheKey{T}"/> would build for <paramref name="key"/>, which arrives normalized, into <paramref name="destination"/> without a string; the caller normalizes the result as <see cref="CacheKey.WithName"/> would. False when it does not fit, or when the strategy cannot compose by span, and the caller builds the key instead.</summary>
    bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written);
}
