namespace UiPath.Caching;

public partial interface ICache : IDisposable
{
    string Name { get; }

    ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy = null, CancellationToken token = default)
        where TState : notnull
        => BatchGetOrAdd.RunAsync(this, entries, generator, (pairs, t) => SetAsync(pairs, policy, t), policy, token);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
        where TState : notnull
        => BatchGetOrAdd.RunAsync(this, entries, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default)
        where TState : notnull
        => BatchGetOrAdd.RunAsync(this, entries, generator, (pairs, t) => SetAsync(pairs, expiration, policy, t), policy, token);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CachePolicy? policy = null, CancellationToken token = default);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);
}
