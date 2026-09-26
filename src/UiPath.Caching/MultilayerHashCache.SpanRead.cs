#if NET9_0_OR_GREATER
namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache
{
    /// <inheritdoc cref="MultilayerCache.TryGetLocal{T}(ReadOnlySpan{char}, out T)"/>
    private bool TryGetLocal<T>(ReadOnlySpan<char> cacheKey, [MaybeNullWhen(false)] out ICacheEntry<IDictionary<string, T?>> entry)
    {
        entry = default;
        if (_memoryCache is not MemoryCache memoryCache || !(_connectionState.IsConnected || _useLocalOnlyWhenDisconnected) || _logger.IsEnabled(LogLevel.Trace))
        {
            return false;
        }

        Span<char> composed = stackalloc char[SpanKey.MaxLength];
        if (!SpanKey.TryCompose<T>(_entryBuilder.KeyStrategy, cacheKey, composed, out var written)
            || !memoryCache.TryGetValue(composed[..written], out var cached)
            || cached is not ICacheEntry<IDictionary<string, T?>> found
            || !(_connectionState.IsConnected || _useLocalOnlyWhenDisconnected))
        {
            return false;
        }

        entry = found;
        return true;
    }
}
#endif
