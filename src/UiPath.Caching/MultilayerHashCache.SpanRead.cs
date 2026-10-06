using System.Collections.Immutable;

namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache
{
    /// <summary>The field from a local entry, only over the dictionary the local tier built itself: its ordinal keys make this the lookup <c>Filter</c> ends in.</summary>
    private static bool TryGetItem<T>(ICacheEntry<IDictionary<string, T?>> entry, string field, out T? value)
    {
        if (entry.Value is ImmutableDictionary<string, T?> { KeyComparer: var comparer } values && ReferenceEquals(comparer, EqualityComparer<string>.Default))
        {
            value = values.TryGetValue(field, out var found) ? found : default;
            return true;
        }

        value = default;
        return false;
    }

#if NET9_0_OR_GREATER
    /// <inheritdoc cref="MultilayerCache.TryGetLocal{T}(ReadOnlySpan{char}, CancellationToken, out ICacheEntry{T})"/>
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
#else
    /// <summary>Always false: <see cref="MemoryCache"/> looks a key up by span only on .NET 9 and later.</summary>
    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Mirrors the .NET 9 signature so callers need no conditional code.")]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "An instance member like the .NET 9 one, so its callers stay instance members on every target.")]
    [SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "An instance member like the .NET 9 one, so its callers stay instance members on every target.")]
    private bool TryGetLocal<T>(ReadOnlySpan<char> cacheKey, [MaybeNullWhen(false)] out ICacheEntry<IDictionary<string, T?>> entry)
    {
        entry = default;
        return false;
    }
#endif

    /// <inheritdoc cref="MultilayerCache.TryGetLocal{T}(CacheKey, CancellationToken, out ICacheEntry{T})"/>
    private bool TryGetLocal<T>(CacheKey cacheKey, [MaybeNullWhen(false)] out ICacheEntry<IDictionary<string, T?>> entry)
    {
        entry = default;
        return cacheKey.Casing == CacheKey.DefaultCasing && TryGetLocal(cacheKey.Name.AsSpan(), out entry);
    }

    /// <summary>A local hit that <see cref="GetOrAddInternalAsync{T}"/> would return as found. False under a rehydrating policy, which needs the <see cref="CacheKey"/>, and for a cancelled token, which the key path reports through its task.</summary>
    private bool TryGetOrAddLocal<T>(ReadOnlySpan<char> cacheKey, CachePolicy policy, CancellationToken token, [MaybeNullWhen(false)] out IDictionary<string, T?> values)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        values = default;
        if ((policy.RehydrateEnabled == true && policy.Rehydrate is not null) || token.IsCancellationRequested || !TryGetLocal<T>(cacheKey, out var entry) || !entry.Found)
        {
            return false;
        }

        values = entry.Value ?? Empty<T>();
        return true;
    }
}
