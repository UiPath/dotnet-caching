namespace UiPath.Platform.Caching;

// Source-compatibility default interface methods for pre-CachePolicy call sites. Each forwards
// to the policy-bearing overload with policy=null. Excluded from coverage — these are forwarders
// with no behavior of their own; the policy-bearing impls are what tests exercise.
public partial interface ICache
{
    [ExcludeFromCodeCoverage]
    ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CancellationToken token = default)
        => GetAsync<T>(cacheKey, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CancellationToken token = default)
        => GetAsync<T>(cacheKeys, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CancellationToken token = default)
        => GetCacheEntryAsync<T>(cacheKey, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CancellationToken token = default)
        => GetCacheEntriesAsync<T>(cacheKeys, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default)
        => GetOrAddAsync<T>(cacheKey, generator, (CachePolicy?)null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan? expiration, CancellationToken token = default)
        => GetOrAddAsync<T>(cacheKey, generator, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, CancellationToken token = default)
        => GetOrAddAsync<T>(cacheKey, generator, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CancellationToken token = default)
        => SetAsync<T>(cacheKey, value, (CachePolicy?)null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration, CancellationToken token = default)
        => SetAsync<T>(cacheKey, value, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration, CancellationToken token = default)
        => SetAsync<T>(cacheKey, value, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token = default)
        => SetAsync<T>(keyValues, (CachePolicy?)null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan? expiration, CancellationToken token = default)
        => SetAsync<T>(keyValues, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset? expiration, CancellationToken token = default)
        => SetAsync<T>(keyValues, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CancellationToken token = default)
        => RefreshAsync<T>(cacheKey, (CachePolicy?)null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default)
        => RefreshAsync<T>(cacheKey, expiration, null, token);

    [ExcludeFromCodeCoverage]
    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default)
        => RefreshAsync<T>(cacheKey, expiration, null, token);
}
