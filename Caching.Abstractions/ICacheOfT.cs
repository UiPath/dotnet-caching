namespace UiPath.Platform.Caching;
public interface ICache<T>
{
    ValueTask<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<ValueTask<T?>> generator, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<ValueTask<T?>> generator, TimeSpan? expiration = null, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<ValueTask<T?>> generator, DateTimeOffset? expiration = null, CancellationToken token = default);

    ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default);

    ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default);
}
