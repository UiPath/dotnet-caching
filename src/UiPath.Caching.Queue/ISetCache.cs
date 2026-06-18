namespace UiPath.Caching;

/// <summary>
/// A distributed cache backed by a Redis set: an unordered collection of unique members.
/// Despite the Caching.Queue package name, this is not a FIFO/LIFO queue — members have no
/// insertion order and PopAsync removes a random member (Redis SPOP). Use the dedicated list
/// caches when order matters.
/// </summary>
public partial interface ISetCache : IDisposable
{
    string Name { get; }

    /// <remarks>
    /// Expiration applies to the whole key, not to individual members (Redis sets have no per-member
    /// TTL). Every AddAsync call re-applies the resolved expiration, so adding any member resets the
    /// TTL of the entire set.
    /// </remarks>
    ValueTask<bool> AddAsync<T>(CacheKey cacheKey, T item, CachePolicy? policy = null, CancellationToken token = default);

    /// <inheritdoc cref="AddAsync{T}(CacheKey, T, CachePolicy?, CancellationToken)"/>
    ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CachePolicy? policy = null, CancellationToken token = default);

    /// <inheritdoc cref="AddAsync{T}(CacheKey, T, CachePolicy?, CancellationToken)"/>
    ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    /// <inheritdoc cref="AddAsync{T}(CacheKey, T, CachePolicy?, CancellationToken)"/>
    ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    /// <summary>
    /// Removes and returns a random member of the set (Redis SPOP). The set is unordered, so this is
    /// not a FIFO/LIFO dequeue — callers must not assume any insertion order.
    /// </summary>
    ValueTask<T?> PopAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default);

    /// <summary>
    /// Removes and returns up to count random members of the set (Redis SPOP). The set is unordered,
    /// so this is not a FIFO/LIFO dequeue — callers must not assume any insertion order.
    /// </summary>
    ValueTask<IReadOnlyCollection<T?>> PopAsync<T>(CacheKey cacheKey, long count, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<IReadOnlyCollection<T?>> MembersAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> ContainsItemAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default);

    ValueTask<long> CountAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveItemAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default);

    ValueTask<long> RemoveItemsAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);
}
