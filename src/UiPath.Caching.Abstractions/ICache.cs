namespace UiPath.Caching;

public interface ICache : IDisposable
{
    string Name { get; }

    ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default);

    ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
        => BatchGetOrAdd.RunAsync(this, entries, generator, (pairs, t) => SetAsync(pairs, policy, t), policy, token);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan? expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
        => BatchGetOrAdd.RunAsync(this, entries, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset? expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull
        => BatchGetOrAdd.RunAsync(this, entries, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset? expiration, CachePolicy? policy, CancellationToken token = default);

    /// <summary>
    /// Writes <paramref name="value"/> only if <paramref name="cacheKey"/> is absent. Redis
    /// <c>SET … NX</c>, one atomic round-trip.
    /// </summary>
    /// <returns>
    /// <c>true</c> only if this call created the key. <c>false</c> conflates "it existed" with "the
    /// write could not be completed", deliberately and fail-closed. Never deletes; not a lock.
    /// </returns>
    ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, CachePolicy, CancellationToken)"/>
    /// <param name="expiration">
    /// Lifetime if the entry is created, applied by the same command. Falls back to
    /// <c>CachePolicy.DistributedExpiration</c> then the cache default. Not in the future: no-op.
    /// </param>
    ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, TimeSpan?, CachePolicy, CancellationToken)"/>
    ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);
}
