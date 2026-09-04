namespace UiPath.Caching;

/// <summary>
/// Source-compatibility overloads for pre-CachePolicy call sites. Each forwards to the
/// policy-bearing <see cref="ICache"/> member with <c>policy: null</c>.
/// </summary>
// Excluded from coverage — forwarders with no behavior of their own; the policy-bearing impls
// are what tests exercise.
[ExcludeFromCodeCoverage]
public static class CacheExtensions
{
    public static ValueTask<T?> GetAsync<T>(this ICache cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.GetAsync<T>(cacheKey, null, token);

    public static ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(this ICache cache, CacheKey[] cacheKeys, CancellationToken token = default)
        => cache.GetAsync<T>(cacheKeys, null, token);

    public static ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(this ICache cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.GetCacheEntryAsync<T>(cacheKey, null, token);

    public static ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(this ICache cache, CacheKey[] cacheKeys, CancellationToken token = default)
        => cache.GetCacheEntriesAsync<T>(cacheKeys, null, token);

    public static ValueTask<T?> GetOrAddAsync<T>(this ICache cache, CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default)
        => cache.GetOrAddAsync<T>(cacheKey, generator, (CachePolicy?)null, token);

    public static ValueTask<T?> GetOrAddAsync<T>(this ICache cache, CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan? expiration, CancellationToken token = default)
        => cache.GetOrAddAsync<T>(cacheKey, generator, expiration, null, token);

    public static ValueTask<T?> GetOrAddAsync<T>(this ICache cache, CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, CancellationToken token = default)
        => cache.GetOrAddAsync<T>(cacheKey, generator, expiration, null, token);

    public static ValueTask<bool> SetAsync<T>(this ICache cache, CacheKey cacheKey, T? value, CancellationToken token = default)
        => cache.SetAsync<T>(cacheKey, value, (CachePolicy?)null, token);

    public static ValueTask<bool> SetAsync<T>(this ICache cache, CacheKey cacheKey, T? value, TimeSpan? expiration, CancellationToken token = default)
        => cache.SetAsync<T>(cacheKey, value, expiration, null, token);

    public static ValueTask<bool> SetAsync<T>(this ICache cache, CacheKey cacheKey, T? value, DateTimeOffset? expiration, CancellationToken token = default)
        => cache.SetAsync<T>(cacheKey, value, expiration, null, token);

    public static ValueTask<bool> SetAsync<T>(this ICache cache, KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token = default)
        => cache.SetAsync<T>(keyValues, (CachePolicy?)null, token);

    public static ValueTask<bool> SetAsync<T>(this ICache cache, KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan? expiration, CancellationToken token = default)
        => cache.SetAsync<T>(keyValues, expiration, null, token);

    public static ValueTask<bool> SetAsync<T>(this ICache cache, KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset? expiration, CancellationToken token = default)
        => cache.SetAsync<T>(keyValues, expiration, null, token);

    /// <inheritdoc cref="ICache.TryAddAsync{T}(CacheKey, T, CachePolicy, CancellationToken)"/>
    public static ValueTask<bool> TryAddAsync<T>(this ICache cache, CacheKey cacheKey, T? value, CancellationToken token = default)
        => cache.TryAddAsync<T>(cacheKey, value, (CachePolicy?)null, token);

    /// <inheritdoc cref="ICache.TryAddAsync{T}(CacheKey, T, TimeSpan?, CachePolicy, CancellationToken)"/>
    public static ValueTask<bool> TryAddAsync<T>(this ICache cache, CacheKey cacheKey, T? value, TimeSpan? expiration, CancellationToken token = default)
        => cache.TryAddAsync<T>(cacheKey, value, expiration, null, token);

    /// <inheritdoc cref="ICache.TryAddAsync{T}(CacheKey, T, TimeSpan?, CachePolicy, CancellationToken)"/>
    public static ValueTask<bool> TryAddAsync<T>(this ICache cache, CacheKey cacheKey, T? value, DateTimeOffset? expiration, CancellationToken token = default)
        => cache.TryAddAsync<T>(cacheKey, value, expiration, null, token);

    public static ValueTask<bool> RefreshAsync<T>(this ICache cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.RefreshAsync<T>(cacheKey, (CachePolicy?)null, token);

    public static ValueTask<bool> RefreshAsync<T>(this ICache cache, CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default)
        => cache.RefreshAsync<T>(cacheKey, expiration, null, token);

    public static ValueTask<bool> RefreshAsync<T>(this ICache cache, CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default)
        => cache.RefreshAsync<T>(cacheKey, expiration, null, token);
}
