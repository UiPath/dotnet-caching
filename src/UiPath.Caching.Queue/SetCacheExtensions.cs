namespace UiPath.Caching;

/// <summary>
/// Source-compatibility overloads for pre-CachePolicy call sites. Each forwards to the
/// policy-bearing <see cref="ISetCache"/> member with <c>policy: null</c>.
/// </summary>
// Excluded from coverage — forwarders with no behavior of their own; the policy-bearing impls
// are what tests exercise.
[ExcludeFromCodeCoverage]
public static class SetCacheExtensions
{
    /// <inheritdoc cref="ISetCache.AddAsync{T}(CacheKey, T, CachePolicy?, CancellationToken)"/>
    public static ValueTask<bool> AddAsync<T>(this ISetCache cache, CacheKey cacheKey, T item, CancellationToken token = default)
        => cache.AddAsync<T>(cacheKey, item, (CachePolicy?)null, token);

    /// <inheritdoc cref="ISetCache.AddAsync{T}(CacheKey, T, CachePolicy?, CancellationToken)"/>
    public static ValueTask<long> AddAsync<T>(this ISetCache cache, CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default)
        => cache.AddAsync<T>(cacheKey, items, (CachePolicy?)null, token);

    /// <inheritdoc cref="ISetCache.AddAsync{T}(CacheKey, T, CachePolicy?, CancellationToken)"/>
    public static ValueTask<long> AddAsync<T>(this ISetCache cache, CacheKey cacheKey, IEnumerable<T> items, TimeSpan expiration, CancellationToken token = default)
        => cache.AddAsync<T>(cacheKey, items, expiration, null, token);

    /// <inheritdoc cref="ISetCache.AddAsync{T}(CacheKey, T, CachePolicy?, CancellationToken)"/>
    public static ValueTask<long> AddAsync<T>(this ISetCache cache, CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset expiration, CancellationToken token = default)
        => cache.AddAsync<T>(cacheKey, items, expiration, null, token);

    /// <inheritdoc cref="ISetCache.PopAsync{T}(CacheKey, CachePolicy?, CancellationToken)"/>
    public static ValueTask<T?> PopAsync<T>(this ISetCache cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.PopAsync<T>(cacheKey, (CachePolicy?)null, token);

    /// <inheritdoc cref="ISetCache.PopAsync{T}(CacheKey, long, CachePolicy?, CancellationToken)"/>
    public static ValueTask<IReadOnlyCollection<T?>> PopAsync<T>(this ISetCache cache, CacheKey cacheKey, long count, CancellationToken token = default)
        => cache.PopAsync<T>(cacheKey, count, null, token);

    /// <inheritdoc cref="ISetCache.MembersAsync{T}(CacheKey, CachePolicy?, CancellationToken)"/>
    public static ValueTask<IReadOnlyCollection<T?>> MembersAsync<T>(this ISetCache cache, CacheKey cacheKey, CancellationToken token = default)
        => cache.MembersAsync<T>(cacheKey, (CachePolicy?)null, token);
}
