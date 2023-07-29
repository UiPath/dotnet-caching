namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public class HashCache<T> : IHashCache<T>
{
    private readonly IHashCache _cache;
    private readonly ICacheKeyStrategy _cacheKeyStrategy;

    public HashCache(ICacheFactory cacheFactory)
    {
        _cache = cacheFactory.CreateHashCache(entityType: typeof(T), callerType: GetType());
        _cacheKeyStrategy = new DefaultCacheKeyStrategy();
    }

    public HashCache(IHashCache cache, ICacheKeyStrategy? cacheKeyStrategy = null)
    {
        _cache = cache;
        _cacheKeyStrategy = cacheKeyStrategy ?? new DefaultCacheKeyStrategy();
    }

    public Task<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ContainsAsync<T>(GetCacheKey(cacheKey), token);

    public Task<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ExpireTimeAsync<T>(GetCacheKey(cacheKey), token);

    public Task<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetAsync<T>(GetCacheKey(cacheKey), token);

    public Task<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        _cache.GetAsync<T>(GetCacheKey(cacheKey), fields, token);

    public Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetCacheEntryAsync<T>(GetCacheKey(cacheKey), token);

    public Task<IDictionary<string, string?>?> GetMetadataAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetMetadataAsync<T>(GetCacheKey(cacheKey), token);

    public Task<T?> GetItemAsync(CacheKey cacheKey, string field, CancellationToken token = default) =>
        _cache.GetItemAsync<T>(GetCacheKey(cacheKey), field, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, token);

    public Task<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, token);

    public Task<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), token);

    public Task<bool> RefreshAsync(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, token);

    public Task<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, token);

    public Task<bool> RefreshAsync(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), options, token);

    public Task<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(GetCacheKey(cacheKey), token);

    public Task<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), values, token);

    public Task<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), values, expiration, token);

    public Task<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), values, expiration, token);

    public Task<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), values, options, token);

    public Task<bool> SetMetadataAsync(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        _cache.SetMetadataAsync<T>(GetCacheKey(cacheKey), metadata, token);

    public Task<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.TimeToLiveAsync<T>(GetCacheKey(cacheKey), token);

    private CacheKey GetCacheKey(CacheKey cacheKey) =>
        _cacheKeyStrategy.GetCacheKey<T>(cacheKey);
}
