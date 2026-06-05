namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public class SetCache<T> : ISetCache<T>
{
    private readonly ISetCache _cache;
    private readonly ICacheKeyStrategy _cacheKeyStrategy;

    public SetCache(ISetCache cache, ICacheKeyStrategy? cacheKeyStrategy = null, ICachePolicyFactory? policyFactory = null, string? policyName = null)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        _cache = cache;
        _cacheKeyStrategy = cacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        Policy = policyFactory?.Resolve(policyName ?? typeof(T).FullName ?? typeof(T).Name);
    }

    public string Name => _cache.Name;

    public CachePolicy? Policy { get; }

    public ValueTask<bool> AddAsync(CacheKey cacheKey, T item, CancellationToken token = default) =>
        _cache.AddAsync<T>(GetCacheKey(cacheKey), item, Policy, token);

    public ValueTask<long> AddAsync(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default) =>
        _cache.AddAsync<T>(GetCacheKey(cacheKey), items, policy: Policy, token: token);

    public ValueTask<long> AddAsync(CacheKey cacheKey, IEnumerable<T> items, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.AddAsync<T>(GetCacheKey(cacheKey), items, expiration, Policy, token);

    public ValueTask<long> AddAsync(CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.AddAsync<T>(GetCacheKey(cacheKey), items, expiration, Policy, token);

    public ValueTask<T?> PopAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.PopAsync<T>(GetCacheKey(cacheKey), Policy, token);

    public ValueTask<IReadOnlyCollection<T?>> PopAsync(CacheKey cacheKey, long count, CancellationToken token = default) =>
        _cache.PopAsync<T>(GetCacheKey(cacheKey), count, Policy, token);

    public ValueTask<IReadOnlyCollection<T?>> MembersAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.MembersAsync<T>(GetCacheKey(cacheKey), Policy, token);

    public ValueTask<bool> ContainsItemAsync(CacheKey cacheKey, T item, CancellationToken token = default) =>
        _cache.ContainsItemAsync<T>(GetCacheKey(cacheKey), item, token);

    public ValueTask<long> CountAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.CountAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<bool> RemoveItemAsync(CacheKey cacheKey, T item, CancellationToken token = default) =>
        _cache.RemoveItemAsync<T>(GetCacheKey(cacheKey), item, token);

    public ValueTask<long> RemoveItemsAsync(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default) =>
        _cache.RemoveItemsAsync<T>(GetCacheKey(cacheKey), items, token);

    public ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ContainsAsync<T>(GetCacheKey(cacheKey), token);

    private CacheKey GetCacheKey(CacheKey cacheKey) =>
        _cacheKeyStrategy.GetCacheKey<T>(cacheKey);
}
