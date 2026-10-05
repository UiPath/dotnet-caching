namespace UiPath.Caching;

/// <summary>An <see cref="ICacheKeyStrategy"/> that composes a key by span, so a span read through it can hit locally without allocating.</summary>
/// <remarks>Apart from <see cref="ICacheKeyStrategy"/>, so a mock of <see cref="ICacheKeyStrategy"/> never has to proxy a span; a strategy without it sends span reads down the string path.</remarks>
public interface ISpanCacheKeyStrategy
{
    /// <summary>Writes the text <see cref="ICacheKeyStrategy.GetCacheKey{T}"/> would build for <paramref name="key"/>, which arrives normalized, into <paramref name="destination"/> without a string; the caller normalizes the result as <see cref="CacheKey.WithName"/> would. False when it does not fit, or when the strategy cannot compose by span, and the caller builds the key instead.</summary>
    bool TryGetCacheKey<T>(ReadOnlySpan<char> key, Span<char> destination, out int written);
}
