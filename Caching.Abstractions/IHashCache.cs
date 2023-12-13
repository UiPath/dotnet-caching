namespace UiPath.Platform.Caching;

public interface IHashCache : IDisposable
{
    ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, T? defaultValue = null, CancellationToken token = default) where T : class;

    ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, T? defaultValue = null, CancellationToken token = default) where T : struct;

    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, IDictionary<string, T?>? defaultValue = null, CancellationToken token = default) where T : class;

    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, IDictionary<string, T?>? defaultValue = null, CancellationToken token = default) where T : struct;

    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, IDictionary<string, T?>? defaultValue = null, CancellationToken token = default) where T : class;

    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, IDictionary<string, T?>? defaultValue = null, CancellationToken token = default) where T : struct;

    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, IDictionary<string, T?>? defaultValue = null, CancellationToken token = default) where T : class;

    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, IDictionary<string, T?>? defaultValue = null, CancellationToken token = default) where T : struct;

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, CancellationToken token = default) where T : class;

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, CancellationToken token = default) where T : struct;

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, TimeSpan? expiration, CancellationToken token = default) where T : class;
 
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, TimeSpan? expiration, CancellationToken token = default) where T : struct;

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, CancellationToken token = default) where T : class;

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, CancellationToken token = default) where T : struct;

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, HashCacheSetOption? setOption, CancellationToken token = default) where T : class;
 
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<ValueTask<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, HashCacheSetOption? setOption, CancellationToken token = default) where T : struct;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default) where T : class;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default) where T : struct;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration, CancellationToken token = default) where T : class;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration, CancellationToken token = default) where T : struct;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration, CancellationToken token = default) where T : class;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration, CancellationToken token = default) where T : struct;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default) where T : class;

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default) where T : struct;

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);
}
