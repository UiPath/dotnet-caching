namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
public class HashCache<T> : IHashCache<T>, ISpanKeyHashCache<T>
{
    private readonly IHashCache _cache;
    private readonly ICacheKeyStrategy _cacheKeyStrategy;

    public HashCache(ICacheFactory cacheFactory, ICacheKeyStrategy? cacheKeyStrategy = null, ICachePolicyFactory? policyFactory = null, string? policyName = null)
    : this(cacheFactory.CreateHashCache(), cacheKeyStrategy, (policyFactory ?? cacheFactory.PolicyFactory)?.Resolve(policyName ?? typeof(T).FullName ?? typeof(T).Name))
    {
    }

    public HashCache(
        IHashCache cache,
        ICacheKeyStrategy? cacheKeyStrategy = null,
        CachePolicy? policy = null)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        _cache = cache;
        _cacheKeyStrategy = cacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        Policy = policy;
    }

    public string Name => _cache.Name;

    /// <summary>
    /// Resolved <see cref="CachePolicy"/> snapshot taken at construction. Supplied directly via the
    /// base ctor's <c>policy</c> argument, or resolved by the DI-injected <c>ICachePolicyFactory</c>
    /// (falling back to <see cref="ICacheFactory.PolicyFactory"/> when no DI factory is registered).
    /// <c>null</c> when neither source is wired — the underlying <see cref="IHashCache"/> then coalesces
    /// to its factory's <c>Default</c> on every call, so cache-wide
    /// <c>CacheOptions.DefaultCachePolicy</c> defaults are still honored.
    /// </summary>
    public CachePolicy? Policy { get; }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetItemAsync(CacheKey cacheKey, string field, CancellationToken token = default)
    {
#if NET9_0_OR_GREATER
        if (ReadsByText(cacheKey))
        {
            Span<char> composed = stackalloc char[SpanKey.MaxLength];
            if (SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey.Name, composed, out var written, CancellationToken.None))
            {
                return _cache.GetItemAsync<T>(composed[..written], field, Policy, token);
            }
        }
#endif
        return _cache.GetItemAsync<T>(GetCacheKey(cacheKey), field, Policy, token);
    }

    public ValueTask<T?> GetItemAsync(Span<char> cacheKey, string field, CancellationToken token = default)
    {
        if (_cacheKeyStrategy is DefaultCacheKeyStrategy)
        {
            return _cache.GetItemAsync<T>(cacheKey, field, Policy, token);
        }

        Span<char> composed = stackalloc char[SpanKey.MaxLength];
        return SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey, composed, out var written, CancellationToken.None)
            ? _cache.GetItemAsync<T>(composed[..written], field, Policy, token)
            : GetItemAsync(new CacheKey(cacheKey), field, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, CancellationToken token = default)
    {
#if NET9_0_OR_GREATER
        if (ReadsByText(cacheKey))
        {
            Span<char> composed = stackalloc char[SpanKey.MaxLength];
            if (SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey.Name, composed, out var written, CancellationToken.None))
            {
                return _cache.GetAsync<T>(composed[..written], Policy, token);
            }
        }
#endif
        return _cache.GetAsync<T>(GetCacheKey(cacheKey), Policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetAsync(Span<char> cacheKey, CancellationToken token = default)
    {
        if (_cacheKeyStrategy is DefaultCacheKeyStrategy)
        {
            return _cache.GetAsync<T>(cacheKey, Policy, token);
        }

        Span<char> composed = stackalloc char[SpanKey.MaxLength];
        return SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey, composed, out var written, CancellationToken.None)
            ? _cache.GetAsync<T>(composed[..written], Policy, token)
            : GetAsync(new CacheKey(cacheKey), token);
    }

    public ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        _cache.GetAsync<T>(GetCacheKey(cacheKey), fields, Policy, token);

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
#if NET9_0_OR_GREATER
        if (ReadsByText(cacheKey))
        {
            Span<char> composed = stackalloc char[SpanKey.MaxLength];
            if (SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey.Name, composed, out var written, CancellationToken.None))
            {
                return _cache.GetOrAddAsync(composed[..written], generator, Policy, token);
            }
        }
#endif
        return _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, Policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default)
    {
#if NET9_0_OR_GREATER
        if (ReadsByText(cacheKey))
        {
            Span<char> composed = stackalloc char[SpanKey.MaxLength];
            if (SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey.Name, composed, out var written, CancellationToken.None))
            {
                return _cache.GetOrAddAsync(composed[..written], generator, expiration, Policy, token);
            }
        }
#endif
        return _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, Policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
#if NET9_0_OR_GREATER
        if (ReadsByText(cacheKey))
        {
            Span<char> composed = stackalloc char[SpanKey.MaxLength];
            if (SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey.Name, composed, out var written, CancellationToken.None))
            {
                return _cache.GetOrAddAsync(composed[..written], generator, expiration, Policy, token);
            }
        }
#endif
        return _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, Policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        if (_cacheKeyStrategy is DefaultCacheKeyStrategy)
        {
            return _cache.GetOrAddAsync(cacheKey, generator, Policy, token);
        }

        Span<char> composed = stackalloc char[SpanKey.MaxLength];
        return SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey, composed, out var written, CancellationToken.None)
            ? _cache.GetOrAddAsync(composed[..written], generator, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        if (_cacheKeyStrategy is DefaultCacheKeyStrategy)
        {
            return _cache.GetOrAddAsync(cacheKey, generator, expiration, Policy, token);
        }

        Span<char> composed = stackalloc char[SpanKey.MaxLength];
        return SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey, composed, out var written, CancellationToken.None)
            ? _cache.GetOrAddAsync(composed[..written], generator, expiration, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        if (_cacheKeyStrategy is DefaultCacheKeyStrategy)
        {
            return _cache.GetOrAddAsync(cacheKey, generator, expiration, Policy, token);
        }

        Span<char> composed = stackalloc char[SpanKey.MaxLength];
        return SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey, composed, out var written, CancellationToken.None)
            ? _cache.GetOrAddAsync(composed[..written], generator, expiration, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(CacheKey cacheKey, CancellationToken token = default)
    {
#if NET9_0_OR_GREATER
        if (ReadsByText(cacheKey))
        {
            Span<char> composed = stackalloc char[SpanKey.MaxLength];
            if (SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey.Name, composed, out var written, CancellationToken.None))
            {
                return _cache.GetCacheEntryAsync<T>(composed[..written], Policy, token);
            }
        }
#endif
        return _cache.GetCacheEntryAsync<T>(GetCacheKey(cacheKey), Policy, token);
    }

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(Span<char> cacheKey, CancellationToken token = default)
    {
        if (_cacheKeyStrategy is DefaultCacheKeyStrategy)
        {
            return _cache.GetCacheEntryAsync<T>(cacheKey, Policy, token);
        }

        Span<char> composed = stackalloc char[SpanKey.MaxLength];
        return SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey, composed, out var written, CancellationToken.None)
            ? _cache.GetCacheEntryAsync<T>(composed[..written], Policy, token)
            : GetCacheEntryAsync(new CacheKey(cacheKey), token);
    }

    public ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), values, policy: Policy, token: token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), values, expiration, Policy, token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), values, expiration, Policy, token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), values, options, Policy, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), policy: Policy, token: token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, Policy, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, Policy, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), options, Policy, token);

    public ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(GetCacheKey(cacheKey), token);

    [OverloadResolutionPriority(1)]
    public ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default)
    {
#if NET9_0_OR_GREATER
        if (ReadsByText(cacheKey))
        {
            Span<char> composed = stackalloc char[SpanKey.MaxLength];
            if (SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey.Name, composed, out var written, CancellationToken.None))
            {
                return _cache.ContainsAsync<T>(composed[..written], token);
            }
        }
#endif
        return _cache.ContainsAsync<T>(GetCacheKey(cacheKey), token);
    }

    public ValueTask<bool> ContainsAsync(Span<char> cacheKey, CancellationToken token = default)
    {
        if (_cacheKeyStrategy is DefaultCacheKeyStrategy)
        {
            return _cache.ContainsAsync<T>(cacheKey, token);
        }

        Span<char> composed = stackalloc char[SpanKey.MaxLength];
        return SpanKey.TryCompose<T>(_cacheKeyStrategy, cacheKey, composed, out var written, CancellationToken.None)
            ? _cache.ContainsAsync<T>(composed[..written], token)
            : ContainsAsync(new CacheKey(cacheKey), token);
    }

    public ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.TimeToLiveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ExpireTimeAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetMetadataAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<bool> SetMetadataAsync(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        _cache.SetMetadataAsync<T>(GetCacheKey(cacheKey), metadata, token);

#if NET9_0_OR_GREATER
    /// <summary>True when the key's text composes to what <see cref="GetCacheKey"/> builds: a strategy that changes it, over a key with the default casing the span path normalizes with.</summary>
    private bool ReadsByText(CacheKey cacheKey) =>
        _cacheKeyStrategy is not DefaultCacheKeyStrategy && cacheKey.Casing == CacheKey.DefaultCasing;
#endif

    private CacheKey GetCacheKey(CacheKey cacheKey) =>
        _cacheKeyStrategy.GetCacheKey<T>(cacheKey);
}
