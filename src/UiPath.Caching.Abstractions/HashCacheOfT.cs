namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
public class HashCache<T> : IHashCache<T>
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

    public ValueTask<T?> GetItemAsync(CacheKey cacheKey, string field, CancellationToken token = default) =>
        _cache.GetItemAsync<T>(GetCacheKey(cacheKey), field, Policy, token);

    public ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetAsync<T>(GetCacheKey(cacheKey), policy: Policy, token: token);

    public ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        _cache.GetAsync<T>(GetCacheKey(cacheKey), fields, Policy, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, policy: Policy, token: token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, Policy, token);

    public ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, Policy, token);

    public ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetCacheEntryAsync<T>(GetCacheKey(cacheKey), policy: Policy, token: token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), values, policy: Policy, token: token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), values, expiration, Policy, token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), values, expiration, Policy, token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), values, options, Policy, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), policy: Policy, token: token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, Policy, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, Policy, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), options, Policy, token);

    public ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ContainsAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.TimeToLiveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ExpireTimeAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<IDictionary<string, string?>?> GetMetadataAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetMetadataAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<bool> SetMetadataAsync(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        _cache.SetMetadataAsync<T>(GetCacheKey(cacheKey), metadata, token);

    private CacheKey GetCacheKey(CacheKey cacheKey) =>
        _cacheKeyStrategy.GetCacheKey<T>(cacheKey);
}
