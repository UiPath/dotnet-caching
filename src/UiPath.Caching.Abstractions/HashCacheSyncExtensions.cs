namespace UiPath.Caching;

/// <summary>
/// Blocking forwarders over the <see cref="IHashCache{T}"/> async API. Each blocks on the
/// underlying call via <c>.AsTask().GetAwaiter().GetResult()</c> — use only from sync call sites
/// that can tolerate the thread-blocking cost.
/// </summary>
// Excluded from coverage — forwarders with no behavior of their own; the async impls are what
// tests exercise.
[ExcludeFromCodeCoverage]
public static class HashCacheSyncExtensions
{
    public static T? GetItem<T>(this IHashCache<T> cache, CacheKey cacheKey, string field, CancellationToken token = default)
        => cache.GetItemAsync(cacheKey, field, token).AsTask().GetAwaiter().GetResult();

    public static IDictionary<string, T?> Get<T>(this IHashCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.GetAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static IDictionary<string, T?> Get<T>(this IHashCache<T> cache, CacheKey cacheKey, string[] fields, CancellationToken token = default)
        => cache.GetAsync(cacheKey, fields, token).AsTask().GetAwaiter().GetResult();

    public static IDictionary<string, T?> GetOrAdd<T>(this IHashCache<T> cache, CacheKey cacheKey, Func<IDictionary<string, T?>> generator, CancellationToken token = default)
        => cache.GetOrAddAsync(cacheKey, _ => Task.FromResult(generator()), token).AsTask().GetAwaiter().GetResult();

    public static IDictionary<string, T?> GetOrAdd<T>(this IHashCache<T> cache, CacheKey cacheKey, Func<IDictionary<string, T?>> generator, TimeSpan expiration, CancellationToken token = default)
        => cache.GetOrAddAsync(cacheKey, _ => Task.FromResult(generator()), expiration, token).AsTask().GetAwaiter().GetResult();

    public static IDictionary<string, T?> GetOrAdd<T>(this IHashCache<T> cache, CacheKey cacheKey, Func<IDictionary<string, T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
        => cache.GetOrAddAsync(cacheKey, _ => Task.FromResult(generator()), expiration, token).AsTask().GetAwaiter().GetResult();

    public static ICacheEntry<IDictionary<string, T?>> GetCacheEntry<T>(this IHashCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.GetCacheEntryAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static bool Set<T>(this IHashCache<T> cache, CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default)
        => cache.SetAsync(cacheKey, values, token).AsTask().GetAwaiter().GetResult();

    public static bool Set<T>(this IHashCache<T> cache, CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CancellationToken token = default)
        => cache.SetAsync(cacheKey, values, expiration, token).AsTask().GetAwaiter().GetResult();

    public static bool Set<T>(this IHashCache<T> cache, CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CancellationToken token = default)
        => cache.SetAsync(cacheKey, values, expiration, token).AsTask().GetAwaiter().GetResult();

    public static bool Set<T>(this IHashCache<T> cache, CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default)
        => cache.SetAsync(cacheKey, values, options, token).AsTask().GetAwaiter().GetResult();

    public static bool Refresh<T>(this IHashCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.RefreshAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static bool Refresh<T>(this IHashCache<T> cache, CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default)
        => cache.RefreshAsync(cacheKey, expiration, token).AsTask().GetAwaiter().GetResult();

    public static bool Refresh<T>(this IHashCache<T> cache, CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default)
        => cache.RefreshAsync(cacheKey, expiration, token).AsTask().GetAwaiter().GetResult();

    public static bool Refresh<T>(this IHashCache<T> cache, CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default)
        => cache.RefreshAsync(cacheKey, options, token).AsTask().GetAwaiter().GetResult();

    public static bool Remove<T>(this IHashCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.RemoveAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static bool Contains<T>(this IHashCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.ContainsAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static TimeSpan? TimeToLive<T>(this IHashCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.TimeToLiveAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static DateTimeOffset? ExpireTime<T>(this IHashCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.ExpireTimeAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static IDictionary<string, string?>? GetMetadata<T>(this IHashCache<T> cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.GetMetadataAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    public static bool SetMetadata<T>(this IHashCache<T> cache, CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default)
        => cache.SetMetadataAsync(cacheKey, metadata, token).AsTask().GetAwaiter().GetResult();
}
