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
        var probe = ProbeLocal<T>(cacheKey, out var found);
        entry = found!;
        return probe == LocalProbe.Hit;
    }

    /// <inheritdoc cref="MultilayerCache.ProbeLocal{T}(ReadOnlySpan{char}, CancellationToken, out ICacheEntry{T})"/>
    private LocalProbe ProbeLocal<T>(ReadOnlySpan<char> cacheKey, out ICacheEntry<IDictionary<string, T?>>? entry)
    {
        entry = default;
        if (_memoryCache is not MemoryCache memoryCache || !(_connectionState.IsConnected || _useLocalOnlyWhenDisconnected) || _logger.IsEnabled(LogLevel.Trace))
        {
            return LocalProbe.Unavailable;
        }

        Span<char> composed = stackalloc char[SpanKey.MaxLength];
        if (!SpanKey.TryCompose<T>(_entryBuilder.KeyStrategy, cacheKey, composed, out var written))
        {
            return LocalProbe.Unavailable;
        }

        if (!memoryCache.TryGetValue(composed[..written], out var cached) || cached is not ICacheEntry<IDictionary<string, T?>> found)
        {
            return LocalProbe.Miss;
        }

        if (!(_connectionState.IsConnected || _useLocalOnlyWhenDisconnected))
        {
            return LocalProbe.Unavailable;
        }

        entry = found;
        return LocalProbe.Hit;
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

    /// <summary>Always <see cref="LocalProbe.Unavailable"/>: <see cref="MemoryCache"/> looks a key up by span only on .NET 9 and later.</summary>
    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Mirrors the .NET 9 signature so callers need no conditional code.")]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "An instance member like the .NET 9 one, so its callers stay instance members on every target.")]
    [SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "An instance member like the .NET 9 one, so its callers stay instance members on every target.")]
    private LocalProbe ProbeLocal<T>(ReadOnlySpan<char> cacheKey, out ICacheEntry<IDictionary<string, T?>>? entry)
    {
        entry = default;
        return LocalProbe.Unavailable;
    }
#endif

    /// <inheritdoc cref="MultilayerCache.TryGetLocal{T}(CacheKey, CancellationToken, out ICacheEntry{T})"/>
    private bool TryGetLocal<T>(CacheKey cacheKey, [MaybeNullWhen(false)] out ICacheEntry<IDictionary<string, T?>> entry)
    {
        var probe = ProbeLocal<T>(cacheKey, out var found);
        entry = found!;
        return probe == LocalProbe.Hit;
    }

    /// <inheritdoc cref="MultilayerCache.ProbeLocal{T}(CacheKey, CancellationToken, out ICacheEntry{T})"/>
    private LocalProbe ProbeLocal<T>(CacheKey cacheKey, out ICacheEntry<IDictionary<string, T?>>? entry)
    {
        entry = default;
        if (cacheKey.Casing != CacheKey.DefaultCasing)
        {
            return LocalProbe.Unavailable;
        }

        if (_entryBuilder.KeyStrategy is DefaultCacheKeyStrategy)
        {
            return ProbeLocalByName(cacheKey, out entry);
        }

#if NET9_0_OR_GREATER
        return ProbeLocal(cacheKey.Name.AsSpan(), out entry);
#else
        return LocalProbe.Unavailable;
#endif
    }

    /// <inheritdoc cref="MultilayerCache.ProbeLocalByName{T}(CacheKey, out ICacheEntry{T})"/>
    private LocalProbe ProbeLocalByName<T>(CacheKey cacheKey, out ICacheEntry<IDictionary<string, T?>>? entry)
    {
        entry = default;
        if (cacheKey.IsNull || !(_connectionState.IsConnected || _useLocalOnlyWhenDisconnected) || _logger.IsEnabled(LogLevel.Trace))
        {
            return LocalProbe.Unavailable;
        }

        if (!_memoryCache.TryGetValue(cacheKey.Name, out var cached) || cached is not ICacheEntry<IDictionary<string, T?>> found)
        {
            return LocalProbe.Miss;
        }

        entry = found;
        return LocalProbe.Hit;
    }

    /// <summary>A local hit that <see cref="GetOrAddInternalAsync{T}"/> would return as found. Not a hit under a rehydrating policy, which needs the <see cref="CacheKey"/>, for a cancelled token, which the key path reports through its task, and for an entry that is not found, which the key path judges.</summary>
    private LocalProbe ProbeGetOrAddLocal<T>(ReadOnlySpan<char> cacheKey, CachePolicy policy, CancellationToken token, out IDictionary<string, T?>? values)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        values = default;
        if (token.IsCancellationRequested)
        {
            return LocalProbe.Unavailable;
        }

        var probe = ProbeLocal<T>(cacheKey, out var entry);
        if (probe != LocalProbe.Hit)
        {
            return probe;
        }

        if ((policy.RehydrateEnabled == true && policy.Rehydrate is not null) || !entry!.Found)
        {
            return LocalProbe.Unavailable;
        }

        values = entry.Value ?? Empty<T>();
        return LocalProbe.Hit;
    }
}
