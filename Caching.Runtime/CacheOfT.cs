namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public class Cache<T> : ICache<T>
{
    private readonly ICache _cache;

    public Cache(ICacheFactory cacheFactory) => 
        _cache = cacheFactory.CreateCache(entityType: typeof(T), callerType: GetType());

    public Cache(ICache cache) =>
        _cache = cache;

    public Task<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ContainsAsync<T>(cacheKey, token);

    public Task<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetAsync<T>(cacheKey, token);

    public Task<T?> GetOrAddAsync(CacheKey cacheKey, Func<Task<T?>> generator, CancellationToken token = default) =>
        _cache.GetOrAddAsync(cacheKey, generator, token);

    public Task<T?> GetOrAddAsync(CacheKey cacheKey, Func<Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.GetOrAddAsync(cacheKey, generator, expiration, token);

    public Task<T?> GetOrAddAsync(CacheKey cacheKey, Func<Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.GetOrAddAsync(cacheKey, generator, expiration, token);

    public Task RefreshAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(cacheKey, token);

    public Task RefreshAsync(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(cacheKey, expiration, token);

    public Task RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(cacheKey, expiration, token);

    public Task<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(cacheKey, token);

    public Task<bool> SetAsync(CacheKey cacheKey, T? value, CancellationToken token = default) =>
        _cache.SetAsync(cacheKey, value, token);

    public Task<bool> SetAsync(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(cacheKey, value, expiration, token);

    public Task<bool> SetAsync(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CancellationToken token = default) =>
        _cache.SetAsync(cacheKey, value, expiration, token);

    public Task<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.TimeToLiveAsync<T>(cacheKey, token);

    public Task<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ExpireTimeAsync<T>(cacheKey, token);
}
