namespace UiPath.Caching;

/// <summary>A caller's key text composed for the local tier without a string.</summary>
internal static class SpanKey
{
    /// <summary>Longest composed key read by span; a longer one takes the string path.</summary>
    internal const int MaxLength = 256;

    /// <summary>Normalizes <paramref name="key"/> as <see cref="CacheKey"/> would, lets <paramref name="strategy"/> compose it, and normalizes the result as <see cref="CacheKey.WithName"/> would, so both sides match the key path. <paramref name="token"/> is observed once the text is known to be non-empty and before the strategy runs, where <c>CacheEntryBuilder</c> observes it. False for an empty key, a strategy that declines, or a result that does not fit.</summary>
    internal static bool TryCompose<T>(ICacheKeyStrategy strategy, ReadOnlySpan<char> key, Span<char> destination, out int written, CancellationToken token = default)
    {
        written = 0;
        Span<char> normalized = stackalloc char[MaxLength];
        if (!CacheKey.TryNormalize(key, normalized, CacheKey.DefaultCasing, out var length) || length == 0)
        {
            return false;
        }

        token.ThrowIfCancellationRequested();
        Span<char> composed = stackalloc char[MaxLength];
        return strategy.TryGetCacheKey<T>(normalized[..length], composed, out var composedLength) && composedLength > 0
            && CacheKey.TryNormalize(composed[..composedLength], destination, CacheKey.DefaultCasing, out written) && written > 0;
    }
}
