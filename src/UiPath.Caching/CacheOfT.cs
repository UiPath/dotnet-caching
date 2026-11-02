namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
public class Cache<T> : ICache<T>, ISpanKeyCache<T>
{
    private readonly ICache _cache;
    private readonly ISpanKeyCache? _spanCache;
    private readonly ICacheKeyStrategy _cacheKeyStrategy;

    public Cache(ICacheFactory cacheFactory, ICacheKeyStrategy? cacheKeyStrategy = null, ICachePolicyFactory? policyFactory = null, string? policyName = null)
    : this(cacheFactory.CreateCache(), cacheKeyStrategy, (policyFactory ?? (cacheFactory as IPolicyDefaults)?.PolicyFactory)?.Resolve(policyName ?? typeof(T).FullName ?? typeof(T).Name))
    {
    }

    public Cache(
        ICache cache,
        ICacheKeyStrategy? cacheKeyStrategy = null,
        CachePolicy? policy = null)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        _cache = cache;
        _spanCache = cache as ISpanKeyCache;
        _cacheKeyStrategy = cacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        Policy = policy;
    }

    public string Name => _cache.Name;

    /// <summary>
    /// Resolved <see cref="CachePolicy"/> snapshot taken at construction. Supplied directly via the
    /// base ctor's <c>policy</c> argument, or resolved by the DI-injected <c>ICachePolicyFactory</c>
    /// (falling back to <see cref="IPolicyDefaults.PolicyFactory"/> when no DI factory is registered).
    /// <c>null</c> when neither source is wired — the underlying <see cref="ICache"/> then coalesces
    /// to its factory's <c>Default</c> on every call, so cache-wide
    /// <c>CacheOptions.DefaultCachePolicy</c> defaults are still honored.
    /// </summary>
    public CachePolicy? Policy { get; }

    [OverloadResolutionPriority(1)]
    public ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default) =>
        SpanKey.Named<T, Cache<T>, ValueTask<bool>>(
            _cacheKeyStrategy,
            this,
            cacheKey,
            static (self, key, t) => self._spanCache is { } c ? c.ContainsAsync<T>(key, t) : self._cache.ContainsAsync<T>(new CacheKey(key), t),
            static (self, k, t) => self._cache.ContainsAsync<T>(self.GetCacheKey(k), t),
            token);

    public ValueTask<bool> ContainsAsync(Span<char> cacheKey, CancellationToken token = default) =>
        SpanKey.Typed<T, Cache<T>, ValueTask<bool>>(
            _cacheKeyStrategy,
            this,
            cacheKey,
            static (self, key, t) => self._spanCache is { } c ? c.ContainsAsync<T>(key, t) : self._cache.ContainsAsync<T>(new CacheKey(key), t),
            static (self, k, t) => self.ContainsAsync(k, t),
            token);

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetAsync<T>(key, Policy, token)
            : _cache.GetAsync<T>(GetCacheKey(cacheKey), Policy, token);
    }

    public ValueTask<T?> GetAsync(Span<char> cacheKey, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetAsync<T>(key, Policy, token)
            : GetAsync(new CacheKey(cacheKey), token);
    }

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync(CacheKey[] cacheKeys, CancellationToken token = default) =>
        _cache.GetAsync<T>(GetCacheKeys(cacheKeys), policy: Policy, token: token);

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, generator, Policy, token)
            : _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, Policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, generator, expiration, Policy, token)
            : _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, Policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, generator, expiration, Policy, token)
            : _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, Policy, token);
    }

    public ValueTask<T?> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, generator, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, token);
    }

    public ValueTask<T?> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, generator, expiration, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);
    }

    public ValueTask<T?> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, generator, expiration, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, state, generator, Policy, token)
            : _cache.GetOrAddAsync(GetCacheKey(cacheKey), state, generator, Policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, state, generator, expiration, Policy, token)
            : _cache.GetOrAddAsync(GetCacheKey(cacheKey), state, generator, expiration, Policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, state, generator, expiration, Policy, token)
            : _cache.GetOrAddAsync(GetCacheKey(cacheKey), state, generator, expiration, Policy, token);
    }

    public ValueTask<T?> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, state, generator, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, token);
    }

    public ValueTask<T?> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, state, generator, expiration, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, token);
    }

    public ValueTask<T?> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddAsync(key, state, generator, expiration, Policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, token);
    }

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CancellationToken token = default)
        where TState : notnull =>
        _cache.GetOrAddAsync<T, TState>(MapKeys(entries), generator, policy: Policy, token: token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CancellationToken token = default)
        where TState : notnull =>
        _cache.GetOrAddAsync<T, TState>(MapKeys(entries), generator, expiration, Policy, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CancellationToken token = default)
        where TState : notnull =>
        _cache.GetOrAddAsync<T, TState>(MapKeys(entries), generator, expiration, Policy, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CancellationToken token = default)
        where TState : notnull =>
        _cache.GetOrAddAsync<T, TState, TArgument>(MapKeys(entries), argument, generator, Policy, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CancellationToken token = default)
        where TState : notnull =>
        _cache.GetOrAddAsync<T, TState, TArgument>(MapKeys(entries), argument, generator, expiration, Policy, token);

    public ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CancellationToken token = default)
        where TState : notnull =>
        _cache.GetOrAddAsync<T, TState, TArgument>(MapKeys(entries), argument, generator, expiration, Policy, token);

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddWithExpirationAsync(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddWithExpirationAsync(key, generator, Policy, token)
            : _cache.GetOrAddWithExpirationAsync(GetCacheKey(cacheKey), generator, Policy, token);
    }

    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetOrAddWithExpirationAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default)
    {
        Span<char> buffer = SpanKey.ComposesName(_cacheKeyStrategy, cacheKey) ? stackalloc char[SpanKey.MaxLength] : default;
        return _spanCache is not null && SpanKey.TryComposeName<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddWithExpirationAsync(key, state, generator, Policy, token)
            : _cache.GetOrAddWithExpirationAsync(GetCacheKey(cacheKey), state, generator, Policy, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync(Span<char> cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddWithExpirationAsync(key, generator, Policy, token)
            : GetOrAddWithExpirationAsync(new CacheKey(cacheKey), generator, token);
    }

    public ValueTask<T?> GetOrAddWithExpirationAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.GetOrAddWithExpirationAsync(key, state, generator, Policy, token)
            : GetOrAddWithExpirationAsync(new CacheKey(cacheKey), state, generator, token);
    }

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), policy: Policy, token: token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, Policy, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, Policy, token);

    public ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<bool> RemoveAsync(CacheKey[] cacheKeys, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(GetCacheKeys(cacheKeys), token);

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

    public ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, CancellationToken token = default) =>
        SetMany(keyValues, static (c, kv, t) => c._cache.SetManyAsync(kv, c.Policy, t), token);

    public ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, TimeSpan expiration, CancellationToken token = default) =>
        SetMany(keyValues, (c, kv, t) => c._cache.SetManyAsync(kv, expiration, c.Policy, t), token);

    public ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, DateTimeOffset expiration, CancellationToken token = default) =>
        SetMany(keyValues, (c, kv, t) => c._cache.SetManyAsync(kv, expiration, c.Policy, t), token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, policy: Policy, token: token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, expiration, Policy, token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, expiration, Policy, token);

    [OverloadResolutionPriority(-1)]
    public ValueTask<bool> SetAsync(Span<char> cacheKey, T? value, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.SetAsync<T>(key, value, Policy, token)
            : SetAsync(new CacheKey(cacheKey), value, token);
    }

    [OverloadResolutionPriority(-1)]
    public ValueTask<bool> SetAsync(Span<char> cacheKey, T? value, TimeSpan expiration, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.SetAsync<T>(key, value, expiration, Policy, token)
            : SetAsync(new CacheKey(cacheKey), value, expiration, token);
    }

    [OverloadResolutionPriority(-1)]
    public ValueTask<bool> SetAsync(Span<char> cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default)
    {
        Span<char> buffer = _cacheKeyStrategy is DefaultCacheKeyStrategy ? default : stackalloc char[SpanKey.MaxLength];
        return _spanCache is not null && SpanKey.TryComposeTyped<T>(_cacheKeyStrategy, cacheKey, buffer, out var key)
            ? _spanCache.SetAsync<T>(key, value, expiration, Policy, token)
            : SetAsync(new CacheKey(cacheKey), value, expiration, token);
    }

    public ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token = default) =>
        _cache.SetAsync(GetKeyValuePairs(keyValues), policy: Policy, token: token);

    public ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetKeyValuePairs(keyValues), expiration, Policy, token);

    public ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetKeyValuePairs(keyValues), expiration, Policy, token);

    public ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, CancellationToken token = default) =>
        _cache.TryAddAsync(GetCacheKey(cacheKey), value, policy: Policy, token: token);

    public ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token = default) =>
        _cache.TryAddAsync(GetCacheKey(cacheKey), value, expiration, Policy, token);

    public ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default) =>
        _cache.TryAddAsync(GetCacheKey(cacheKey), value, expiration, Policy, token);


    public ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.TimeToLiveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<TimeSpan?> TimeToLiveAsync(Span<char> cacheKey, CancellationToken token = default) =>
        SpanKey.Typed<T, Cache<T>, ValueTask<TimeSpan?>>(
            _cacheKeyStrategy,
            this,
            cacheKey,
            static (self, key, t) => self._spanCache is { } c ? c.TimeToLiveAsync<T>(key, t) : self._cache.TimeToLiveAsync<T>(new CacheKey(key), t),
            static (self, k, t) => self.TimeToLiveAsync(k, t),
            token);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ExpireTimeAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(Span<char> cacheKey, CancellationToken token = default) =>
        SpanKey.Typed<T, Cache<T>, ValueTask<DateTimeOffset?>>(
            _cacheKeyStrategy,
            this,
            cacheKey,
            static (self, key, t) => self._spanCache is { } c ? c.ExpireTimeAsync<T>(key, t) : self._cache.ExpireTimeAsync<T>(new CacheKey(key), t),
            static (self, k, t) => self.ExpireTimeAsync(k, t),
            token);

    private static async ValueTask<IReadOnlyList<CacheKey>> RestoreAsync<TItem>(ValueTask<IReadOnlyList<CacheKey>> pending, ComposedKeys<TItem> composed) =>
        composed.Restore(await pending.ConfigureAwait(false));

    private ValueTask<IReadOnlyList<CacheKey>> SetMany(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, Func<Cache<T>, IEnumerable<KeyValuePair<CacheKey, T?>>, CancellationToken, ValueTask<IReadOnlyList<CacheKey>>> set, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(keyValues);
        if (_cacheKeyStrategy is DefaultCacheKeyStrategy)
        {
            return set(this, keyValues, token);
        }

        var composed = ComposedKeys<KeyValuePair<CacheKey, T?>>.Create(keyValues, static kv => kv.Key, GetCacheKey, static (kv, k) => new KeyValuePair<CacheKey, T?>(k, kv.Value));
        return RestoreAsync(set(this, composed, token), composed);
    }

    /// <summary>Applies the key strategy to each entry's key and leaves its state alone.</summary>
    private KeyValuePair<CacheKey, TState>[] MapKeys<TState>(KeyValuePair<CacheKey, TState>[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Array.ConvertAll(entries, e => new KeyValuePair<CacheKey, TState>(GetCacheKey(e.Key), e.Value));
    }

    private CacheKey GetCacheKey(CacheKey cacheKey) =>
        _cacheKeyStrategy.GetCacheKey<T>(cacheKey);

    private CacheKey[] GetCacheKeys(CacheKey[] cacheKeys) =>
        Array.ConvertAll(cacheKeys, GetCacheKey);

    private KeyValuePair<CacheKey, T?>[] GetKeyValuePairs(KeyValuePair<CacheKey, T?>[] keyValues) =>
        Array.ConvertAll(keyValues, kv => new KeyValuePair<CacheKey, T?>(GetCacheKey(kv.Key), kv.Value));
}

