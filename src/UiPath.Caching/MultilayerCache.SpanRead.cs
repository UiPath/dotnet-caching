namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    /// <summary>A local hit by span, or false whenever the string path is needed: another <see cref="IMemoryCache"/>, a strategy that declines, an empty or overlong key, a disconnected inner tier not served locally, or Trace logging, which renders the key. Cancellation is observed where <see cref="CacheEntryBuilder"/> observes it: once the key is known non-empty, before the strategy.</summary>
    private bool TryGetLocal<T>(ReadOnlySpan<char> cacheKey, CancellationToken token, [MaybeNullWhen(false)] out ICacheEntry<T> entry)
    {
        var probe = ProbeLocal<T>(cacheKey, token, out var found);
        entry = found!;
        return probe == LocalProbe.Hit;
    }

    /// <summary>A local hit by the key's text, for a key that <see cref="SpanKey.TryCompose{T}"/> would normalize as it was built: one with the default casing.</summary>
    private bool TryGetLocal<T>(CacheKey cacheKey, CancellationToken token, [MaybeNullWhen(false)] out ICacheEntry<T> entry)
    {
        var probe = ProbeLocal<T>(cacheKey, token, out var found);
        entry = found!;
        return probe == LocalProbe.Hit;
    }

#if NET9_0_OR_GREATER
    /// <summary>The read behind <see cref="TryGetLocal{T}(ReadOnlySpan{char}, CancellationToken, out ICacheEntry{T})"/>, which also says when the tier definitively holds nothing.</summary>
    private LocalProbe ProbeLocal<T>(ReadOnlySpan<char> cacheKey, CancellationToken token, out ICacheEntry<T>? entry)
    {
        entry = default;
        if (_memoryCache is not MemoryCache memoryCache || !(_connectionState.IsConnected || _useLocalOnlyWhenDisconnected) || _logger.IsEnabled(LogLevel.Trace))
        {
            return LocalProbe.Unavailable;
        }

        Span<char> composed = stackalloc char[SpanKey.MaxLength];
        if (!SpanKey.TryCompose<T>(_entryBuilder.KeyStrategy, cacheKey, composed, out var written, token))
        {
            return LocalProbe.Unavailable;
        }

        if (!memoryCache.TryGetValue(composed[..written], out var cached) || cached is not ICacheEntry<T> found)
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
    /// <summary>Always <see cref="LocalProbe.Unavailable"/>: <see cref="MemoryCache"/> looks a key up by span only on .NET 9 and later.</summary>
    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Mirrors the .NET 9 signature so callers need no conditional code.")]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "An instance member like the .NET 9 one, so its callers stay instance members on every target.")]
    [SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "An instance member like the .NET 9 one, so its callers stay instance members on every target.")]
    [SuppressMessage("Major Code Smell", "S1172:Unused method parameters should be removed", Justification = "Mirrors the .NET 9 signature so callers need no conditional code.")]
    private LocalProbe ProbeLocal<T>(ReadOnlySpan<char> cacheKey, CancellationToken token, out ICacheEntry<T>? entry)
    {
        entry = default;
        return LocalProbe.Unavailable;
    }
#endif

    /// <summary>The read behind <see cref="TryGetLocal{T}(CacheKey, CancellationToken, out ICacheEntry{T})"/>, which also says when the tier definitively holds nothing.</summary>
    private LocalProbe ProbeLocal<T>(CacheKey cacheKey, CancellationToken token, out ICacheEntry<T>? entry)
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

        return ProbeLocal(cacheKey.Name.AsSpan(), token, out entry);
    }

    /// <summary>The read by the key's text, which <see cref="DefaultCacheKeyStrategy"/> leaves as the tier stores it: no composing, so a miss costs one lookup and a hit allocates nothing and binds no state.</summary>
    private LocalProbe ProbeLocalByName<T>(CacheKey cacheKey, out ICacheEntry<T>? entry)
    {
        entry = default;
        if (cacheKey.IsNull || !(_connectionState.IsConnected || _useLocalOnlyWhenDisconnected) || _logger.IsEnabled(LogLevel.Trace))
        {
            return LocalProbe.Unavailable;
        }

        if (!_memoryCache.TryGetValue(cacheKey.Name, out var cached) || cached is not ICacheEntry<T> found)
        {
            return LocalProbe.Miss;
        }

        entry = found;
        return LocalProbe.Hit;
    }

    /// <summary>A local hit that <see cref="GetOrAddInternalAsync{T}"/> would return as found. Not a hit under a rehydrating policy, which needs the <see cref="CacheKey"/>, for a cancelled token, which the key path reports through its task, and for an entry that is not found, which the key path judges.</summary>
    private LocalProbe ProbeGetOrAddLocal<T>(ReadOnlySpan<char> cacheKey, CachePolicy policy, CancellationToken token, out T? value)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        value = default;
        if (token.IsCancellationRequested)
        {
            return LocalProbe.Unavailable;
        }

        var probe = ProbeLocal<T>(cacheKey, token, out var entry);
        if (probe != LocalProbe.Hit)
        {
            return probe;
        }

        if ((policy.RehydrateEnabled == true && policy.Rehydrate is not null) || !entry!.Found)
        {
            return LocalProbe.Unavailable;
        }

        value = entry.Value;
        return LocalProbe.Hit;
    }
}
