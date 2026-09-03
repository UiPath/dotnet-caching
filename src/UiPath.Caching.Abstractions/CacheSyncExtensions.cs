namespace UiPath.Caching;

/// <summary>
/// Blocking forwarders over the <see cref="ICache{T}"/> async API. Each blocks on the underlying
/// call via <c>.AsTask().GetAwaiter().GetResult()</c> — use only from sync call sites that can
/// tolerate the thread-blocking cost.
/// </summary>
// Excluded from coverage — forwarders with no behavior of their own; the async impls are what
// tests exercise.
[ExcludeFromCodeCoverage]
public static class CacheSyncExtensions
{
    public static T? Get<T>(this ICache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.GetAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static KeyValuePair<CacheKey, T?>[] Get<T>(this ICache<T> cache, CacheKey[] cacheKeys, CancellationToken token = default)
        => cache.GetAsync(cacheKeys, token).AsTask().GetAwaiter().GetResult();

    public static T? GetOrAdd<T>(this ICache<T> cache, CacheKey cacheKey, Func<T?> generator, CancellationToken token = default)
        => cache.GetOrAddAsync(cacheKey, _ => Task.FromResult(generator()), token).AsTask().GetAwaiter().GetResult();

    public static T? GetOrAdd<T>(this ICache<T> cache, CacheKey cacheKey, Func<T?> generator, TimeSpan expiration, CancellationToken token = default)
        => cache.GetOrAddAsync(cacheKey, _ => Task.FromResult(generator()), expiration, token).AsTask().GetAwaiter().GetResult();

    public static T? GetOrAdd<T>(this ICache<T> cache, CacheKey cacheKey, Func<T?> generator, DateTimeOffset expiration, CancellationToken token = default)
        => cache.GetOrAddAsync(cacheKey, _ => Task.FromResult(generator()), expiration, token).AsTask().GetAwaiter().GetResult();

    public static KeyValuePair<CacheKey, T?>[] GetOrAdd<T>(this ICache<T> cache, CacheKey[] cacheKeys, Func<CacheKey[], KeyValuePair<CacheKey, T?>[]> generator, CancellationToken token = default)
        => cache.GetOrAddAsync(
                Array.ConvertAll(cacheKeys, k => new KeyValuePair<CacheKey, CacheKey>(k, k)),
                (keys, _) => Task.FromResult(generator(keys)),
                token)
            .AsTask().GetAwaiter().GetResult();

    public static KeyValuePair<CacheKey, T?>[] GetOrAdd<T>(this ICache<T> cache, CacheKey[] cacheKeys, Func<CacheKey[], KeyValuePair<CacheKey, T?>[]> generator, TimeSpan expiration, CancellationToken token = default)
        => cache.GetOrAddAsync(
                Array.ConvertAll(cacheKeys, k => new KeyValuePair<CacheKey, CacheKey>(k, k)),
                (keys, _) => Task.FromResult(generator(keys)),
                expiration,
                token)
            .AsTask().GetAwaiter().GetResult();

    public static KeyValuePair<CacheKey, T?>[] GetOrAdd<T>(this ICache<T> cache, CacheKey[] cacheKeys, Func<CacheKey[], KeyValuePair<CacheKey, T?>[]> generator, DateTimeOffset expiration, CancellationToken token = default)
        => cache.GetOrAddAsync(
                Array.ConvertAll(cacheKeys, k => new KeyValuePair<CacheKey, CacheKey>(k, k)),
                (keys, _) => Task.FromResult(generator(keys)),
                expiration,
                token)
            .AsTask().GetAwaiter().GetResult();

    public static bool Remove<T>(this ICache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.RemoveAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static bool Remove<T>(this ICache<T> cache, CacheKey[] cacheKeys, CancellationToken token = default)
        => cache.RemoveAsync(cacheKeys, token).AsTask().GetAwaiter().GetResult();

    public static bool Set<T>(this ICache<T> cache, CacheKey cacheKey, T? value, CancellationToken token = default)
        => cache.SetAsync(cacheKey, value, token).AsTask().GetAwaiter().GetResult();

    public static bool Set<T>(this ICache<T> cache, CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token = default)
        => cache.SetAsync(cacheKey, value, expiration, token).AsTask().GetAwaiter().GetResult();

    public static bool Set<T>(this ICache<T> cache, CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default)
        => cache.SetAsync(cacheKey, value, expiration, token).AsTask().GetAwaiter().GetResult();

    public static bool Set<T>(this ICache<T> cache, KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token = default)
        => cache.SetAsync(keyValues, token).AsTask().GetAwaiter().GetResult();

    public static bool Set<T>(this ICache<T> cache, KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CancellationToken token = default)
        => cache.SetAsync(keyValues, expiration, token).AsTask().GetAwaiter().GetResult();

    public static bool Set<T>(this ICache<T> cache, KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CancellationToken token = default)
        => cache.SetAsync(keyValues, expiration, token).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc cref="ICache{T}.TryAddAsync(CacheKey, T, CancellationToken)"/>
    public static bool TryAdd<T>(this ICache<T> cache, CacheKey cacheKey, T? value, CancellationToken token = default)
        => cache.TryAddAsync(cacheKey, value, token).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc cref="ICache{T}.TryAddAsync(CacheKey, T, TimeSpan, CancellationToken)"/>
    public static bool TryAdd<T>(this ICache<T> cache, CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token = default)
        => cache.TryAddAsync(cacheKey, value, expiration, token).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc cref="ICache{T}.TryAddAsync(CacheKey, T, TimeSpan, CancellationToken)"/>
    public static bool TryAdd<T>(this ICache<T> cache, CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default)
        => cache.TryAddAsync(cacheKey, value, expiration, token).AsTask().GetAwaiter().GetResult();

    public static bool Refresh<T>(this ICache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.RefreshAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static bool Refresh<T>(this ICache<T> cache, CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default)
        => cache.RefreshAsync(cacheKey, expiration, token).AsTask().GetAwaiter().GetResult();

    public static bool Refresh<T>(this ICache<T> cache, CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default)
        => cache.RefreshAsync(cacheKey, expiration, token).AsTask().GetAwaiter().GetResult();

    public static bool Contains<T>(this ICache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.ContainsAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static TimeSpan? TimeToLive<T>(this ICache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.TimeToLiveAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static DateTimeOffset? ExpireTime<T>(this ICache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.ExpireTimeAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();
}
