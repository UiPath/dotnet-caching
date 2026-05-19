namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public class Cache<T> : ICache<T>
{
    private readonly ICache _cache;
    private readonly ICacheKeyStrategy _cacheKeyStrategy;

    public Cache(ICacheFactory cacheFactory, ICachePolicyFactory? policyFactory = null, string? name = null)
        : this(cacheFactory.CreateCache(), cacheKeyStrategy: null, policyFactory, name)
    {
    }

    public Cache(
        ICache cache,
        ICacheKeyStrategy? cacheKeyStrategy = null,
        ICachePolicyFactory? policyFactory = null,
        string? name = null)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        _cache = cache;
        _cacheKeyStrategy = cacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        PolicyName = name ?? typeof(T).FullName ?? typeof(T).Name;
        Policy = policyFactory?.Resolve(PolicyName);
    }

    public string Name => _cache.Name;

    /// <summary>
    /// Cache instance name used for policy resolution. Defaults to <c>typeof(T).FullName ?? typeof(T).Name</c>.
    /// For closed generics like <c>Cache&lt;Dictionary&lt;string,int&gt;&gt;</c>, <c>FullName</c> is a long
    /// assembly-qualified string that's awkward to hand-write in <c>appsettings.json</c> under
    /// <c>CacheOptions.Policies</c> — pass an explicit <c>name:</c> at construction in that case.
    /// </summary>
    public string PolicyName { get; }

    /// <summary>
    /// Resolved <see cref="CachePolicy"/> captured at construction when an <c>ICachePolicyFactory</c>
    /// was supplied. <c>null</c> when no factory is wired — the underlying <see cref="ICache"/>
    /// coalesces to its factory's <c>Default</c> on every call, so cache-wide
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

