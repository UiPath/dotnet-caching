namespace UiPath.Caching;

public interface IHashCache : IDisposable
{
    string Name { get; }

    ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default);

    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);
}
