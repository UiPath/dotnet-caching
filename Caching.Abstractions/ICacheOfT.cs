namespace UiPath.Platform.Caching;
public interface ICache<T>
{
    Task<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default);

    Task<T?> GetOrAddAsync(CacheKey cacheKey, Func<Task<T?>> generator, CancellationToken token = default);

    Task<T?> GetOrAddAsync(CacheKey cacheKey, Func<Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default);

    Task<T?> GetOrAddAsync(CacheKey cacheKey, Func<Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    Task<bool> SetAsync(CacheKey cacheKey, T? value, CancellationToken token = default);

    Task<bool> SetAsync(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    Task RefreshAsync(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default);

    Task RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);

    Task<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default);

    Task<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default);
}
