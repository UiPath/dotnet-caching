namespace UiPath.Platform.Caching;

public interface IHashCache<T>
{
    Task<T?> GetItemAsync(CacheKey cacheKey, string field, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default);

    Task<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(CacheKey cacheKey, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, TimeSpan? expiration = null, CancellationToken token = default);

    Task<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default);

    Task<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default);

    Task<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    Task<bool> RefreshAsync(CacheKey cacheKey, TimeSpan? expiration = null, CancellationToken token = default);

    Task<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default);

    Task<bool> RefreshAsync(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default);

    Task<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    Task<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);

    Task<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default);

    Task<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default);

    Task<IDictionary<string, string?>?> GetMetadataAsync(CacheKey cacheKey, CancellationToken token = default);

    Task<bool> SetMetadataAsync(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);
}
