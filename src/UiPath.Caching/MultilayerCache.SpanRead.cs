namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
#if NET9_0_OR_GREATER
    /// <summary>A local hit by span, or false whenever the string path is needed: another <see cref="IMemoryCache"/>, a strategy that declines, an empty or overlong key, a disconnected inner tier not served locally, or Trace logging, which renders the key. Cancellation is observed where <see cref="CacheEntryBuilder"/> observes it: once the key is known non-empty, before the strategy.</summary>
    private bool TryGetLocal<T>(ReadOnlySpan<char> cacheKey, CancellationToken token, [MaybeNullWhen(false)] out ICacheEntry<T> entry)
    {
        entry = default;
        if (_memoryCache is not MemoryCache memoryCache || !(_connectionState.IsConnected || _useLocalOnlyWhenDisconnected) || _logger.IsEnabled(LogLevel.Trace))
        {
            return false;
        }

        Span<char> composed = stackalloc char[SpanKey.MaxLength];
        if (!SpanKey.TryCompose<T>(_entryBuilder.KeyStrategy, cacheKey, composed, out var written, token)
            || !memoryCache.TryGetValue(composed[..written], out var cached)
            || cached is not ICacheEntry<T> found
            || !(_connectionState.IsConnected || _useLocalOnlyWhenDisconnected))
        {
            return false;
        }

        entry = found;
        return true;
    }
#else
    /// <summary>Always false: <see cref="MemoryCache"/> looks a key up by span only on .NET 9 and later.</summary>
    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Mirrors the .NET 9 signature so callers need no conditional code.")]
    private static bool TryGetLocal<T>(ReadOnlySpan<char> cacheKey, CancellationToken token, [MaybeNullWhen(false)] out ICacheEntry<T> entry)
    {
        entry = default;
        return false;
    }
#endif

    /// <summary>A local hit by the key's text, for a key that <see cref="SpanKey.TryCompose{T}"/> would normalize as it was built: one with the default casing.</summary>
    private bool TryGetLocal<T>(CacheKey cacheKey, CancellationToken token, [MaybeNullWhen(false)] out ICacheEntry<T> entry)
    {
        entry = default;
        return cacheKey.Casing == CacheKey.DefaultCasing && TryGetLocal(cacheKey.Name.AsSpan(), token, out entry);
    }

    /// <summary>A local hit that <see cref="GetOrAddInternalAsync{T}"/> would return as found. False under a rehydrating policy, which needs the <see cref="CacheKey"/>, and for a cancelled token, which the key path reports through its task.</summary>
    private bool TryGetOrAddLocal<T>(ReadOnlySpan<char> cacheKey, CachePolicy policy, CancellationToken token, out T? value)
    {
        value = default;
        if ((policy.RehydrateEnabled == true && policy.Rehydrate is not null) || token.IsCancellationRequested || !TryGetLocal<T>(cacheKey, token, out var entry) || !entry.Found)
        {
            return false;
        }

        value = entry.Value;
        return true;
    }
}
