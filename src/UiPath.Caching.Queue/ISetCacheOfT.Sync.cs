namespace UiPath.Caching;

// Sync forwarders over the async API. Each default interface method blocks on the underlying
// async call via .AsTask().GetAwaiter().GetResult() — use only from sync call sites that can
// tolerate the thread-blocking cost. Excluded from coverage — forwarders with no behavior of
// their own; the async impls are what tests exercise.
public partial interface ISetCache<T>
{
    [ExcludeFromCodeCoverage]
    bool Add(CacheKey cacheKey, T item, CancellationToken token = default)
        => AddAsync(cacheKey, item, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    long Add(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default)
        => AddAsync(cacheKey, items, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    long Add(CacheKey cacheKey, IEnumerable<T> items, TimeSpan? expiration, CancellationToken token = default)
        => AddAsync(cacheKey, items, expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    long Add(CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset? expiration, CancellationToken token = default)
        => AddAsync(cacheKey, items, expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    T? Pop(CacheKey cacheKey, CancellationToken token = default)
        => PopAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    IReadOnlyCollection<T?> Pop(CacheKey cacheKey, long count, CancellationToken token = default)
        => PopAsync(cacheKey, count, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    IReadOnlyCollection<T?> Members(CacheKey cacheKey, CancellationToken token = default)
        => MembersAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool ContainsItem(CacheKey cacheKey, T item, CancellationToken token = default)
        => ContainsItemAsync(cacheKey, item, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    long Count(CacheKey cacheKey, CancellationToken token = default)
        => CountAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool RemoveItem(CacheKey cacheKey, T item, CancellationToken token = default)
        => RemoveItemAsync(cacheKey, item, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    long RemoveItems(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default)
        => RemoveItemsAsync(cacheKey, items, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Remove(CacheKey cacheKey, CancellationToken token = default)
        => RemoveAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Contains(CacheKey cacheKey, CancellationToken token = default)
        => ContainsAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();
}
