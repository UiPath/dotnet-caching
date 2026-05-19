namespace UiPath.Platform.Caching;

// Source-compatibility default interface methods for pre-CachePolicy call sites. Each forwards
// to the policy-bearing overload with policy=null. Excluded from coverage — these are forwarders
// with no behavior of their own; the policy-bearing impls are what tests exercise.
public partial interface IHashCache
{
    [ExcludeFromCodeCoverage]
    ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CancellationToken token = default)
        => GetItemAsync<T>(cacheKey, field, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default)
        => GetAsync<T>(cacheKey, (CachePolicy?)null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default)
        => GetAsync<T>(cacheKey, fields, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CancellationToken token = default)
        => GetCacheEntryAsync<T>(cacheKey, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
        => GetOrAddAsync<T>(cacheKey, generator, (CachePolicy?)null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan? expiration, CancellationToken token = default)
        => GetOrAddAsync<T>(cacheKey, generator, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, CancellationToken token = default)
        => GetOrAddAsync<T>(cacheKey, generator, expiration, (CachePolicy?)null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset? expiration, HashCacheSetOption? setOption, CancellationToken token = default)
        => GetOrAddAsync<T>(cacheKey, generator, expiration, setOption, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default)
        => SetAsync<T>(cacheKey, values, (CachePolicy?)null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration, CancellationToken token = default)
        => SetAsync<T>(cacheKey, values, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration, CancellationToken token = default)
        => SetAsync<T>(cacheKey, values, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default)
        => SetAsync<T>(cacheKey, values, options, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default)
        => RefreshAsync<T>(cacheKey, (CachePolicy?)null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default)
        => RefreshAsync<T>(cacheKey, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default)
        => RefreshAsync<T>(cacheKey, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default)
        => RefreshAsync<T>(cacheKey, options, null, token);
}
