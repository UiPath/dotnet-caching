namespace UiPath.Caching;

/// <summary>
/// Source-compatibility overloads for pre-CachePolicy call sites. Each forwards to the
/// policy-bearing <see cref="IHashCache"/> member with <c>policy: null</c>.
/// </summary>
// Excluded from coverage — forwarders with no behavior of their own; the policy-bearing impls
// are what tests exercise.
[ExcludeFromCodeCoverage]
public static class HashCacheExtensions
{
    public static ValueTask<T?> GetItemAsync<T>(this IHashCache cache, CacheKey cacheKey, string field, CancellationToken token = default)
        => cache.GetItemAsync<T>(cacheKey, field, null, token);

    public static ValueTask<IDictionary<string, T?>> GetAsync<T>(this IHashCache cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.GetAsync<T>(cacheKey, (CachePolicy?)null, token);

    public static ValueTask<IDictionary<string, T?>> GetAsync<T>(this IHashCache cache, CacheKey cacheKey, string[] fields, CancellationToken token = default)
        => cache.GetAsync<T>(cacheKey, fields, null, token);

    public static ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(this IHashCache cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.GetCacheEntryAsync<T>(cacheKey, null, token);

    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(this IHashCache cache, CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
        => cache.GetOrAddAsync<T>(cacheKey, generator, (CachePolicy?)null, token);

    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(this IHashCache cache, CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default)
        => cache.GetOrAddAsync<T>(cacheKey, generator, expiration, null, token);

    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(this IHashCache cache, CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default)
        => cache.GetOrAddAsync<T>(cacheKey, generator, expiration, (CachePolicy?)null, token);

    public static ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(this IHashCache cache, CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CancellationToken token = default)
        => cache.GetOrAddAsync<T>(cacheKey, generator, expiration, setOption, null, token);

    public static ValueTask<bool> SetAsync<T>(this IHashCache cache, CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default)
        => cache.SetAsync<T>(cacheKey, values, (CachePolicy?)null, token);

    public static ValueTask<bool> SetAsync<T>(this IHashCache cache, CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CancellationToken token = default)
        => cache.SetAsync<T>(cacheKey, values, expiration, null, token);

    public static ValueTask<bool> SetAsync<T>(this IHashCache cache, CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CancellationToken token = default)
        => cache.SetAsync<T>(cacheKey, values, expiration, null, token);

    public static ValueTask<bool> SetAsync<T>(this IHashCache cache, CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default)
        => cache.SetAsync<T>(cacheKey, values, options, null, token);

    public static ValueTask<bool> RefreshAsync<T>(this IHashCache cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.RefreshAsync<T>(cacheKey, (CachePolicy?)null, token);

    public static ValueTask<bool> RefreshAsync<T>(this IHashCache cache, CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default)
        => cache.RefreshAsync<T>(cacheKey, expiration, null, token);

    public static ValueTask<bool> RefreshAsync<T>(this IHashCache cache, CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default)
        => cache.RefreshAsync<T>(cacheKey, expiration, null, token);

    public static ValueTask<bool> RefreshAsync<T>(this IHashCache cache, CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default)
        => cache.RefreshAsync<T>(cacheKey, options, null, token);
}
