namespace UiPath.Caching;

/// <summary>A caller's key text composed for the local tier without a string.</summary>
internal static class SpanKey
{
    /// <summary>Longest composed key read by span; a longer one takes the string path.</summary>
    internal const int MaxLength = 256;

    /// <summary>True when a typed cache composes <paramref name="key"/>'s text on the stack: on .NET 9 and later, under a strategy that changes the key, for a key with the default casing the span path normalizes with.</summary>
    internal static bool ComposesName(ICacheKeyStrategy strategy, CacheKey key) =>
#if NET9_0_OR_GREATER
        strategy is not DefaultCacheKeyStrategy && key.Casing == CacheKey.DefaultCasing;
#else
        false;
#endif

    /// <summary>The strategy's composition of <paramref name="key"/>'s text in <paramref name="destination"/>, which is empty unless <see cref="ComposesName"/>; false when it is empty or the strategy declines.</summary>
    internal static bool TryComposeName<T>(ICacheKeyStrategy strategy, CacheKey key, Span<char> destination, out Span<char> composed)
    {
        if (destination.IsEmpty || !TryCompose<T>(strategy, key.Name, destination, out var written, CancellationToken.None))
        {
            composed = default;
            return false;
        }

        composed = destination[..written];
        return true;
    }

    /// <summary>The text a typed cache reads by: the caller's own under <see cref="DefaultCacheKeyStrategy"/>, otherwise the strategy's composition in <paramref name="destination"/>. False when the strategy declines.</summary>
    internal static bool TryComposeTyped<T>(ICacheKeyStrategy strategy, Span<char> text, Span<char> destination, out Span<char> composed)
    {
        if (strategy is DefaultCacheKeyStrategy)
        {
            composed = text;
            return true;
        }

        var fits = TryCompose<T>(strategy, text, destination, out var written, CancellationToken.None);
        composed = fits ? destination[..written] : default;
        return fits;
    }

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
        return strategy is ISpanCacheKeyStrategy spanStrategy
            && spanStrategy.TryGetCacheKey<T>(normalized[..length], composed, out var composedLength) && composedLength > 0
            && CacheKey.TryNormalize(composed[..composedLength], destination, CacheKey.DefaultCasing, out written) && written > 0;
    }
}
