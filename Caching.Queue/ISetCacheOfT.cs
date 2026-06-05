namespace UiPath.Platform.Caching;

/// <summary>
/// A strongly-typed distributed cache backed by a Redis set: an unordered collection of unique
/// members. Despite the Caching.Queue package name, this is not a FIFO/LIFO queue — members have no
/// insertion order and PopAsync removes a random member (Redis SPOP). Use the dedicated list caches
/// when order matters.
/// </summary>
public partial interface ISetCache<T>
{
    string Name { get; }

    /// <remarks>
    /// Expiration applies to the whole key, not to individual members (Redis sets have no per-member
    /// TTL). Every AddAsync call re-applies the resolved expiration, so adding any member resets the
    /// TTL of the entire set.
    /// </remarks>
    ValueTask<bool> AddAsync(CacheKey cacheKey, T item, CancellationToken token = default);

    /// <inheritdoc cref="AddAsync(CacheKey, T, CancellationToken)"/>
    ValueTask<long> AddAsync(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default);

    /// <inheritdoc cref="AddAsync(CacheKey, T, CancellationToken)"/>
    ValueTask<long> AddAsync(CacheKey cacheKey, IEnumerable<T> items, TimeSpan? expiration, CancellationToken token = default);

    /// <inheritdoc cref="AddAsync(CacheKey, T, CancellationToken)"/>
    ValueTask<long> AddAsync(CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset? expiration, CancellationToken token = default);

    /// <summary>
    /// Removes and returns a random member of the set (Redis SPOP). The set is unordered, so this is
    /// not a FIFO/LIFO dequeue — callers must not assume any insertion order.
    /// </summary>
    ValueTask<T?> PopAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <summary>
    /// Removes and returns up to count random members of the set (Redis SPOP). The set is unordered,
    /// so this is not a FIFO/LIFO dequeue — callers must not assume any insertion order.
    /// </summary>
    ValueTask<IReadOnlyCollection<T?>> PopAsync(CacheKey cacheKey, long count, CancellationToken token = default);

    ValueTask<IReadOnlyCollection<T?>> MembersAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> ContainsItemAsync(CacheKey cacheKey, T item, CancellationToken token = default);

    ValueTask<long> CountAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveItemAsync(CacheKey cacheKey, T item, CancellationToken token = default);

    ValueTask<long> RemoveItemsAsync(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default);

    ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);
}
