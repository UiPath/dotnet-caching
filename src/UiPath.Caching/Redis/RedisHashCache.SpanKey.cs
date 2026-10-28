namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    ValueTask<T?> ISpanKeyHashCache.GetItemAsync<T>(Span<char> cacheKey, string field, CachePolicy? policy, CancellationToken token) where T : default =>
        GetItemAsync<T>(new CacheKey(cacheKey), field, policy, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token) where T : default =>
        GetAsync<T>(new CacheKey(cacheKey), policy, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetAsync<T>(Span<char> cacheKey, string[] fields, CachePolicy? policy, CancellationToken token) where T : default =>
        GetAsync<T>(new CacheKey(cacheKey), fields, policy, token);

    ValueTask<ICacheEntry<IDictionary<string, T?>>> ISpanKeyHashCache.GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token) where T : default =>
        GetCacheEntryAsync<T>(new CacheKey(cacheKey), policy, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token) where T : default =>
        GetOrAddAsync(new CacheKey(cacheKey), generator, policy, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token) where T : default =>
        GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, policy, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token) where T : default =>
        GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, policy, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token) where T : default =>
        GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, setOption, policy, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token) where T : default =>
        GetOrAddAsync(new CacheKey(cacheKey), state, generator, policy, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token) where T : default =>
        GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, policy, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token) where T : default =>
        GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, policy, token);

    ValueTask<IDictionary<string, T?>> ISpanKeyHashCache.GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token) where T : default =>
        GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, setOption, policy, token);

    ValueTask<bool> ISpanKeyHashCache.ContainsAsync<T>(Span<char> cacheKey, CancellationToken token) =>
        ContainsAsync<T>(new CacheKey(cacheKey), token);

    ValueTask<TimeSpan?> ISpanKeyHashCache.TimeToLiveAsync<T>(Span<char> cacheKey, CancellationToken token) =>
        TimeToLiveAsync<T>(new CacheKey(cacheKey), token);

    ValueTask<DateTimeOffset?> ISpanKeyHashCache.ExpireTimeAsync<T>(Span<char> cacheKey, CancellationToken token) =>
        ExpireTimeAsync<T>(new CacheKey(cacheKey), token);

    ValueTask<IDictionary<string, string?>?> ISpanKeyHashCache.GetMetadataAsync<T>(Span<char> cacheKey, CancellationToken token) =>
        GetMetadataAsync<T>(new CacheKey(cacheKey), token);

    ValueTask<bool> ISpanKeyHashCache.SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token) where T : default =>
        SetAsync(new CacheKey(cacheKey), values, policy, token);

    ValueTask<bool> ISpanKeyHashCache.SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token) where T : default =>
        SetAsync(new CacheKey(cacheKey), values, expiration, policy, token);

    ValueTask<bool> ISpanKeyHashCache.SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token) where T : default =>
        SetAsync(new CacheKey(cacheKey), values, expiration, policy, token);

    ValueTask<bool> ISpanKeyHashCache.SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token) where T : default =>
        SetAsync(new CacheKey(cacheKey), values, options, policy, token);
}
