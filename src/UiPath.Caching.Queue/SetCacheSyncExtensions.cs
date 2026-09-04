namespace UiPath.Caching;

/// <summary>
/// Blocking forwarders over the <see cref="ISetCache{T}"/> async API. Each blocks on the
/// underlying call via <c>.AsTask().GetAwaiter().GetResult()</c> — use only from sync call sites
/// that can tolerate the thread-blocking cost.
/// </summary>
// Excluded from coverage — forwarders with no behavior of their own; the async impls are what
// tests exercise.
[ExcludeFromCodeCoverage]
public static class SetCacheSyncExtensions
{
    /// <inheritdoc cref="ISetCache{T}.AddAsync(CacheKey, T, CancellationToken)"/>
    public static bool Add<T>(this ISetCache<T> cache, CacheKey cacheKey, T item, CancellationToken token = default)
        => cache.AddAsync(cacheKey, item, token).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc cref="ISetCache{T}.AddAsync(CacheKey, T, CancellationToken)"/>
    public static long Add<T>(this ISetCache<T> cache, CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default)
        => cache.AddAsync(cacheKey, items, token).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc cref="ISetCache{T}.AddAsync(CacheKey, T, CancellationToken)"/>
    public static long Add<T>(this ISetCache<T> cache, CacheKey cacheKey, IEnumerable<T> items, TimeSpan? expiration, CancellationToken token = default)
        => cache.AddAsync(cacheKey, items, expiration, token).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc cref="ISetCache{T}.AddAsync(CacheKey, T, CancellationToken)"/>
    public static long Add<T>(this ISetCache<T> cache, CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset? expiration, CancellationToken token = default)
        => cache.AddAsync(cacheKey, items, expiration, token).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc cref="ISetCache{T}.PopAsync(CacheKey, CancellationToken)"/>
    public static T? Pop<T>(this ISetCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.PopAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc cref="ISetCache{T}.PopAsync(CacheKey, long, CancellationToken)"/>
    public static IReadOnlyCollection<T?> Pop<T>(this ISetCache<T> cache, CacheKey cacheKey, long count, CancellationToken token = default)
        => cache.PopAsync(cacheKey, count, token).AsTask().GetAwaiter().GetResult();

    public static IReadOnlyCollection<T?> Members<T>(this ISetCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.MembersAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static bool ContainsItem<T>(this ISetCache<T> cache, CacheKey cacheKey, T item, CancellationToken token = default)
        => cache.ContainsItemAsync(cacheKey, item, token).AsTask().GetAwaiter().GetResult();

    public static long Count<T>(this ISetCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.CountAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static bool RemoveItem<T>(this ISetCache<T> cache, CacheKey cacheKey, T item, CancellationToken token = default)
        => cache.RemoveItemAsync(cacheKey, item, token).AsTask().GetAwaiter().GetResult();

    public static long RemoveItems<T>(this ISetCache<T> cache, CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default)
        => cache.RemoveItemsAsync(cacheKey, items, token).AsTask().GetAwaiter().GetResult();

    public static bool Remove<T>(this ISetCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.RemoveAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static bool Contains<T>(this ISetCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.ContainsAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();
}
