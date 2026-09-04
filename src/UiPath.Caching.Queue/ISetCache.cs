namespace UiPath.Caching;

/// <summary>
/// A distributed cache backed by a Redis set: an unordered collection of unique members.
/// Despite the Caching.Queue package name, this is not a FIFO/LIFO queue — members have no
/// insertion order and PopAsync removes a random member (Redis SPOP). Use the dedicated list
/// caches when order matters.
/// </summary>
/// <remarks>
/// <para><b>Expiration.</b> The <c>expiration</c> parameter is not nullable. A caller with nothing to
/// say about lifetime calls the overload that has no <c>expiration</c> parameter and gets
/// <see cref="CachePolicy.DistributedExpiration"/>, then the cache default; a caller that passes one
/// means it, so a duration that is not positive — or a deadline that has already passed — is
/// rejected with <see cref="ArgumentOutOfRangeException"/> rather than silently ignored. See
/// <see cref="CacheExpiration"/>. The no-op implementations in this package read no argument at all
/// and so enforce nothing.</para>
/// </remarks>
public interface ISetCache : IDisposable
{
    string Name { get; }

    /// <remarks>
    /// Expiration applies to the whole key, not to individual members (Redis sets have no per-member
    /// TTL). Every AddAsync call re-applies the resolved expiration, so adding any member resets the
    /// TTL of the entire set.
    /// </remarks>
    ValueTask<bool> AddAsync<T>(CacheKey cacheKey, T item, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="AddAsync{T}(CacheKey, T, CachePolicy?, CancellationToken)"/>
    ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="AddAsync{T}(CacheKey, T, CachePolicy?, CancellationToken)"/>
    ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="AddAsync{T}(CacheKey, T, CachePolicy?, CancellationToken)"/>
    ValueTask<long> AddAsync<T>(CacheKey cacheKey, IEnumerable<T> items, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <summary>
    /// Removes and returns a random member of the set (Redis SPOP). The set is unordered, so this is
    /// not a FIFO/LIFO dequeue — callers must not assume any insertion order.
    /// </summary>
    ValueTask<T?> PopAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <summary>
    /// Removes and returns up to count random members of the set (Redis SPOP). The set is unordered,
    /// so this is not a FIFO/LIFO dequeue — callers must not assume any insertion order.
    /// </summary>
    ValueTask<IReadOnlyCollection<T?>> PopAsync<T>(CacheKey cacheKey, long count, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IReadOnlyCollection<T?>> MembersAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> ContainsItemAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default);

    ValueTask<long> CountAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveItemAsync<T>(CacheKey cacheKey, T item, CancellationToken token = default);

    ValueTask<long> RemoveItemsAsync<T>(CacheKey cacheKey, IEnumerable<T> items, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);
}
