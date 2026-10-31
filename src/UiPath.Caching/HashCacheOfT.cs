namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
public class HashCache<T> : IHashCache<T>, ISpanKeyHashCache<T>
{
    private readonly IHashCache _cache;
    private readonly ISpanKeyHashCache? _spanCache;
    private readonly ICacheKeyStrategy _cacheKeyStrategy;

    public HashCache(ICacheFactory cacheFactory, ICacheKeyStrategy? cacheKeyStrategy = null, ICachePolicyFactory? policyFactory = null, string? policyName = null)
    : this(cacheFactory.CreateHashCache(), cacheKeyStrategy, (policyFactory ?? (cacheFactory as IPolicyDefaults)?.PolicyFactory)?.Resolve(policyName ?? typeof(T).FullName ?? typeof(T).Name))
    {
    }

    public HashCache(
        IHashCache cache,
        ICacheKeyStrategy? cacheKeyStrategy = null,
        CachePolicy? policy = null)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        _cache = cache;
        _spanCache = cache as ISpanKeyHashCache;
        _cacheKeyStrategy = cacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        Policy = policy;
    }

    public string Name => _cache.Name;

    /// <summary>
    /// Resolved <see cref="CachePolicy"/> snapshot taken at construction. Supplied directly via the
    /// base ctor's <c>policy</c> argument, or resolved by the DI-injected <c>ICachePolicyFactory</c>
    /// (falling back to <see cref="IPolicyDefaults.PolicyFactory"/> when no DI factory is registered).
    /// <c>null</c> when neither source is wired — the underlying <see cref="IHashCache"/> then coalesces
    /// to its factory's <c>Default</c> on every call, so cache-wide
    /// <c>CacheOptions.DefaultCachePolicy</c> defaults are still honored.
    /// </summary>
    public CachePolicy? Policy { get; }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetItemAsync(CacheKey cacheKey, string field, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetItemAsync<T>(key, field, Policy, token)
            : _cache.GetItemAsync<T>(GetCacheKey(cacheKey), field, Policy, token);
    }

    public ValueTask<T?> GetItemAsync(Span<char> cacheKey, string field, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetItemAsync<T>(key, field, Policy, token)
            : GetItemAsync(new CacheKey(cacheKey), field, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetAsync<T>(key, Policy, token)
            : _cache.GetAsync<T>(GetCacheKey(cacheKey), Policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetAsync(Span<char> cacheKey, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetAsync<T>(key, Policy, token)
            : GetAsync(new CacheKey(cacheKey), token);
    }

    public ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        _cache.GetAsync<T>(GetCacheKey(cacheKey), fields, Policy, token);

    public ValueTask<IDictionary<string, T?>> GetAsync(Span<char> cacheKey, string[] fields, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetAsync<T>(key, fields, Policy, token)
            : GetAsync(new CacheKey(cacheKey), fields, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, generator, Policy, token)
            : _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, Policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, generator, expiration, Policy, token)
            : _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, Policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, generator, expiration, Policy, token)
            : _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, Policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, generator, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, generator, expiration, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, generator, expiration, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, state, generator, Policy, token)
            : _cache.GetOrAddAsync(GetCacheKey(cacheKey), state, generator, Policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, state, generator, expiration, Policy, token)
            : _cache.GetOrAddAsync(GetCacheKey(cacheKey), state, generator, expiration, Policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, state, generator, expiration, Policy, token)
            : _cache.GetOrAddAsync(GetCacheKey(cacheKey), state, generator, expiration, Policy, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, state, generator, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, state, generator, expiration, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, token);
    }

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, state, generator, expiration, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(CacheKey cacheKey, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetCacheEntryAsync<T>(key, Policy, token)
            : _cache.GetCacheEntryAsync<T>(GetCacheKey(cacheKey), Policy, token);
    }

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(Span<char> cacheKey, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetCacheEntryAsync<T>(key, Policy, token)
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

    [OverloadResolutionPriority(-1)]
    public ValueTask<bool> SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.SetAsync<T>(key, values, Policy, token)
            : SetAsync(new CacheKey(cacheKey), values, token);
    }

    [OverloadResolutionPriority(-1)]
    public ValueTask<bool> SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.SetAsync<T>(key, values, expiration, Policy, token)
            : SetAsync(new CacheKey(cacheKey), values, expiration, token);
    }

    [OverloadResolutionPriority(-1)]
    public ValueTask<bool> SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.SetAsync<T>(key, values, expiration, Policy, token)
            : SetAsync(new CacheKey(cacheKey), values, expiration, token);
    }

    [OverloadResolutionPriority(-1)]
    public ValueTask<bool> SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.SetAsync<T>(key, values, options, Policy, token)
            : SetAsync(new CacheKey(cacheKey), values, options, token);
    }

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

    public ValueTask<bool> RemoveFieldsAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        _cache.RemoveFieldsAsync<T>(GetCacheKey(cacheKey), fields, token);

    public ValueTask<IReadOnlyList<CacheKey>> RemoveManyAsync(IEnumerable<CacheKey> cacheKeys, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(cacheKeys);
        if (_cacheKeyStrategy is DefaultCacheKeyStrategy)
        {
            return _cache.RemoveManyAsync<T>(cacheKeys, token);
        }

        var composed = ComposedKeys<CacheKey>.Create(cacheKeys, static k => k, GetCacheKey, static (_, k) => k);
        return RestoreAsync(_cache.RemoveManyAsync<T>(composed, token), composed);
    }

    public ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, IDictionary<string, T?>>> keyValues, HashCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(keyValues);
        if (_cacheKeyStrategy is DefaultCacheKeyStrategy)
        {
            return _cache.SetManyAsync(keyValues, options, Policy, token);
        }

        var composed = ComposedKeys<KeyValuePair<CacheKey, IDictionary<string, T?>>>.Create(keyValues, static kv => kv.Key, GetCacheKey, static (kv, k) => new KeyValuePair<CacheKey, IDictionary<string, T?>>(k, kv.Value));
        return RestoreAsync(_cache.SetManyAsync(composed, options, Policy, token), composed);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default) =>
        SpanKey.Named<T, HashCache<T>, ValueTask<bool>>(
            _cacheKeyStrategy,
            this,
            cacheKey,
            static (self, key, t) => self._spanCache is { } c ? c.ContainsAsync<T>(key, t) : self._cache.ContainsAsync<T>(new CacheKey(key), t),
            static (self, k, t) => self._cache.ContainsAsync<T>(self.GetCacheKey(k), t),
            token);

    public ValueTask<bool> ContainsAsync(Span<char> cacheKey, CancellationToken token = default) =>
        SpanKey.Typed<T, HashCache<T>, ValueTask<bool>>(
            _cacheKeyStrategy,
            this,
            cacheKey,
            static (self, key, t) => self._spanCache is { } c ? c.ContainsAsync<T>(key, t) : self._cache.ContainsAsync<T>(new CacheKey(key), t),
            static (self, k, t) => self.ContainsAsync(k, t),
            token);

    public ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.TimeToLiveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<TimeSpan?> TimeToLiveAsync(Span<char> cacheKey, CancellationToken token = default) =>
        SpanKey.Typed<T, HashCache<T>, ValueTask<TimeSpan?>>(
            _cacheKeyStrategy,
            this,
            cacheKey,
            static (self, key, t) => self._spanCache is { } c ? c.TimeToLiveAsync<T>(key, t) : self._cache.TimeToLiveAsync<T>(new CacheKey(key), t),
            static (self, k, t) => self.TimeToLiveAsync(k, t),
            token);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ExpireTimeAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(Span<char> cacheKey, CancellationToken token = default) =>
        SpanKey.Typed<T, HashCache<T>, ValueTask<DateTimeOffset?>>(
            _cacheKeyStrategy,
            this,
            cacheKey,
            static (self, key, t) => self._spanCache is { } c ? c.ExpireTimeAsync<T>(key, t) : self._cache.ExpireTimeAsync<T>(new CacheKey(key), t),
            static (self, k, t) => self.ExpireTimeAsync(k, t),
            token);

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetMetadataAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync(Span<char> cacheKey, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetMetadataAsync<T>(key, token)
            : GetMetadataAsync(new CacheKey(cacheKey), token);
    }

    public ValueTask<bool> SetMetadataAsync(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        _cache.SetMetadataAsync<T>(GetCacheKey(cacheKey), metadata, token);

    private static async ValueTask<IReadOnlyList<CacheKey>> RestoreAsync<TItem>(ValueTask<IReadOnlyList<CacheKey>> pending, ComposedKeys<TItem> composed) =>
        composed.Restore(await pending.ConfigureAwait(false));

    private CacheKey GetCacheKey(CacheKey cacheKey) =>
        _cacheKeyStrategy.GetCacheKey<T>(cacheKey);
}
