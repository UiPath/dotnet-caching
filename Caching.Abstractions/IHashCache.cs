namespace UiPath.Platform.Caching;

public interface IHashCache : IDisposable
{
    Task<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default);

    Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, HashCacheSetOption? setOption = null, CancellationToken token = default);

    Task<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default);

    Task<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default);

    Task<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default);

    Task<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    Task<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);
}
