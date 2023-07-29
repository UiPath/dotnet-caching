namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public class Cache<T> : ICache<T>
{
    private readonly ICache _cache;
    private readonly ICacheKeyStrategy _cacheKeyStrategy;

    public Cache(ICacheFactory cacheFactory)
    {
        _cache = cacheFactory.CreateCache(entityType: typeof(T), callerType: GetType());
        _cacheKeyStrategy = new DefaultCacheKeyStrategy();
    }

    public Cache(ICache cache, ICacheKeyStrategy? cacheKeyStrategy = null)
    {
        _cache = cache;
        _cacheKeyStrategy = cacheKeyStrategy ?? new DefaultCacheKeyStrategy();
    }

    public Task<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ContainsAsync<T>(GetCacheKey(cacheKey), token);

    public Task<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetAsync<T>(GetCacheKey(cacheKey), token);

    public Task<T?> GetOrAddAsync(CacheKey cacheKey, Func<Task<T?>> generator, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, token);

    public Task<T?> GetOrAddAsync(CacheKey cacheKey, Func<Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, token);

    public Task<T?> GetOrAddAsync(CacheKey cacheKey, Func<Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, token);

    public Task RefreshAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), token);

    public Task RefreshAsync(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, token);

    public Task RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, token);

    public Task<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(GetCacheKey(cacheKey), token);

    public Task<bool> SetAsync(CacheKey cacheKey, T? value, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, token);

    public Task<bool> SetAsync(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, expiration, token);

    public Task<bool> SetAsync(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, expiration, token);

    public Task<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.TimeToLiveAsync<T>(GetCacheKey(cacheKey), token);

    public Task<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ExpireTimeAsync<T>(GetCacheKey(cacheKey), token);

    private CacheKey GetCacheKey(CacheKey cacheKey) =>
        _cacheKeyStrategy.GetCacheKey<T>(cacheKey);
}
