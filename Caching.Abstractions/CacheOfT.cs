namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public class Cache<T> : ICache<T>
{
    private readonly ICache _cache;
    private readonly ICacheKeyStrategy _cacheKeyStrategy;

    public Cache(ICacheFactory cacheFactory, ICacheKeyStrategy? cacheKeyStrategy = null, ICachePolicyFactory? policyFactory = null, string? policyName = null)
    : this(cacheFactory.CreateCache(), cacheKeyStrategy, (policyFactory ?? cacheFactory.PolicyFactory)?.Resolve(policyName ?? typeof(T).FullName ?? typeof(T).Name))
    {
    }

    public Cache(
        ICache cache,
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
    /// <c>null</c> when neither source is wired — the underlying <see cref="ICache"/> then coalesces
    /// to its factory's <c>Default</c> on every call, so cache-wide
    /// <c>CacheOptions.DefaultCachePolicy</c> defaults are still honored.
    /// </summary>
    public CachePolicy? Policy { get; }

    public ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ContainsAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetAsync<T>(GetCacheKey(cacheKey), policy: Policy, token: token);

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync(CacheKey[] cacheKeys, CancellationToken token = default) =>
        _cache.GetAsync<T>(GetCacheKeys(cacheKeys), policy: Policy, token: token);

    public ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, policy: Policy, token: token);

    public ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, Policy, token);

    public ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, Policy, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), policy: Policy, token: token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, Policy, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, Policy, token);

    public ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<bool> RemoveAsync(CacheKey[] cacheKeys, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(GetCacheKeys(cacheKeys), token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, policy: Policy, token: token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, expiration, Policy, token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, expiration, Policy, token);

    public ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token = default) =>
        _cache.SetAsync(GetKeyValuePairs(keyValues), policy: Policy, token: token);

    public ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(GetKeyValuePairs(keyValues), expiration, Policy, token);

    public ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(GetKeyValuePairs(keyValues), expiration, Policy, token);
 
    public ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.TimeToLiveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ExpireTimeAsync<T>(GetCacheKey(cacheKey), token);

    private CacheKey GetCacheKey(CacheKey cacheKey) =>
        _cacheKeyStrategy.GetCacheKey<T>(cacheKey);

    private CacheKey[] GetCacheKeys(CacheKey[] cacheKeys) =>
        cacheKeys.Select(GetCacheKey).ToArray();

    private KeyValuePair<CacheKey, T?>[] GetKeyValuePairs(KeyValuePair<CacheKey, T?>[] keyValues) =>
        keyValues.Select(kv => new KeyValuePair<CacheKey, T?>(GetCacheKey(kv.Key), kv.Value)).ToArray();
}

