namespace UiPath.Platform.Caching;

public interface ICache : IDisposable
{
    Task<T?> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<T?>> generator, CancellationToken token = default);

    Task<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default);

    Task<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default);

    Task<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);
}
