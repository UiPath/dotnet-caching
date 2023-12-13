namespace UiPath.Platform.Caching;

public interface ICache : IDisposable
{
    ValueTask<T?> GetAsync<T>(CacheKey cacheKey, T? defaultValue = null, CancellationToken token = default) where T : class;

    ValueTask<T?> GetAsync<T>(CacheKey cacheKey, T? defaultValue = null, CancellationToken token = default) where T : struct;

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, CancellationToken token = default) where T: class;

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, CancellationToken token = default) where T: struct;

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, TimeSpan? expiration, CancellationToken token = default) where T:struct;

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, TimeSpan? expiration, CancellationToken token = default) where T:class;

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, DateTimeOffset? expiration, CancellationToken token = default) where T: class;

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<T?>> generator, DateTimeOffset? expiration, CancellationToken token = default) where T : struct;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default) where T: class;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default) where T: struct;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration, CancellationToken token = default) where T: class;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration, CancellationToken token = default) where T: struct;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration, CancellationToken token = default) where T: class;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration, CancellationToken token = default) where T: struct;

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);
}
