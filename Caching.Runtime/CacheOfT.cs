namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public class Cache<T> : ICache<T>
    where T : class
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

    public ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ContainsAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetAsync<T>(GetCacheKey(cacheKey), null, token);

    public ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<ValueTask<T?>> generator, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, token);

    public ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<ValueTask<T?>> generator, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, token);

    public ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<ValueTask<T?>> generator, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, token);

    public ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, expiration, token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, expiration, token);

    public ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.TimeToLiveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ExpireTimeAsync<T>(GetCacheKey(cacheKey), token);

    private CacheKey GetCacheKey(CacheKey cacheKey) =>
        _cacheKeyStrategy.GetCacheKey<T>(cacheKey);
}


[ExcludeFromCodeCoverage]
public class StructCache<T> : IStructCache<T>
    where T : struct
{
    private readonly ICache _cache;
    private readonly ICacheKeyStrategy _cacheKeyStrategy;

    public StructCache(ICacheFactory cacheFactory)
    {
        _cache = cacheFactory.CreateCache(entityType: typeof(T), callerType: GetType());
        _cacheKeyStrategy = new DefaultCacheKeyStrategy();
    }

    public StructCache(ICache cache, ICacheKeyStrategy? cacheKeyStrategy = null)
    {
        _cache = cache;
        _cacheKeyStrategy = cacheKeyStrategy ?? new DefaultCacheKeyStrategy();
    }

    public ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ContainsAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.GetAsync<T>(GetCacheKey(cacheKey), default, token);

    public ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<ValueTask<T?>> generator, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, token);

    public ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<ValueTask<T?>> generator, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, token);

    public ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<ValueTask<T?>> generator, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.GetOrAddAsync(GetCacheKey(cacheKey), generator, expiration, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, token);

    public ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.RefreshAsync<T>(GetCacheKey(cacheKey), expiration, token);

    public ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.RemoveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, TimeSpan? expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, expiration, token);

    public ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, DateTimeOffset? expiration, CancellationToken token = default) =>
        _cache.SetAsync(GetCacheKey(cacheKey), value, expiration, token);

    public ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.TimeToLiveAsync<T>(GetCacheKey(cacheKey), token);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default) =>
        _cache.ExpireTimeAsync<T>(GetCacheKey(cacheKey), token);

    private CacheKey GetCacheKey(CacheKey cacheKey) =>
        _cacheKeyStrategy.GetCacheKey<T>(cacheKey);
}
