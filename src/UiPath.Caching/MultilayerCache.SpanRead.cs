#if NET9_0_OR_GREATER
namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    /// <summary>A local hit by span, or false whenever the string path is needed: another <see cref="IMemoryCache"/>, a strategy that declines, an empty or overlong key, a disconnected inner tier not served locally, or Trace logging, which renders the key. Cancellation is observed where <see cref="CacheEntryBuilder"/> observes it: once the key is known non-empty, before the strategy.</summary>
    private bool TryGetLocal<T>(ReadOnlySpan<char> cacheKey, CancellationToken token, out T? value)
    {
        value = default;
        if (_memoryCache is not MemoryCache memoryCache || !(_connectionState.IsConnected || _useLocalOnlyWhenDisconnected) || _logger.IsEnabled(LogLevel.Trace))
        {
            return false;
        }

        Span<char> composed = stackalloc char[SpanKey.MaxLength];
        if (!SpanKey.TryCompose<T>(_entryBuilder.KeyStrategy, cacheKey, composed, out var written, token)
            || !memoryCache.TryGetValue(composed[..written], out var cached)
            || cached is not ICacheEntry<T> entry
            || !(_connectionState.IsConnected || _useLocalOnlyWhenDisconnected))
        {
            return false;
        }

        value = entry.Value;
        return true;
    }
}
#endif
