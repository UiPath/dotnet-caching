namespace UiPath.Platform.Caching;

// Sync forwarders over the async API. Each default interface method blocks on the underlying
// async call via .AsTask().GetAwaiter().GetResult() — use only from sync call sites that can
// tolerate the thread-blocking cost. Excluded from coverage — forwarders with no behavior of
// their own; the async impls are what tests exercise.
public partial interface ICache<T>
{
    [ExcludeFromCodeCoverage]
    T? Get(CacheKey cacheKey, CancellationToken token = default)
        => GetAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    KeyValuePair<CacheKey, T?>[] Get(CacheKey[] cacheKeys, CancellationToken token = default)
        => GetAsync(cacheKeys, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    T? GetOrAdd(CacheKey cacheKey, Func<T?> generator, CancellationToken token = default)
        => GetOrAddAsync(cacheKey, _ => Task.FromResult(generator()), token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    T? GetOrAdd(CacheKey cacheKey, Func<T?> generator, TimeSpan? expiration, CancellationToken token = default)
        => GetOrAddAsync(cacheKey, _ => Task.FromResult(generator()), expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    T? GetOrAdd(CacheKey cacheKey, Func<T?> generator, DateTimeOffset? expiration, CancellationToken token = default)
        => GetOrAddAsync(cacheKey, _ => Task.FromResult(generator()), expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Remove(CacheKey cacheKey, CancellationToken token = default)
        => RemoveAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Remove(CacheKey[] cacheKeys, CancellationToken token = default)
        => RemoveAsync(cacheKeys, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Set(CacheKey cacheKey, T? value, CancellationToken token = default)
        => SetAsync(cacheKey, value, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Set(CacheKey cacheKey, T? value, TimeSpan? expiration, CancellationToken token = default)
        => SetAsync(cacheKey, value, expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Set(CacheKey cacheKey, T? value, DateTimeOffset? expiration, CancellationToken token = default)
        => SetAsync(cacheKey, value, expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Set(KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token = default)
        => SetAsync(keyValues, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Set(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan? expiration = null, CancellationToken token = default)
        => SetAsync(keyValues, expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Set(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset? expiration = null, CancellationToken token = default)
        => SetAsync(keyValues, expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Refresh(CacheKey cacheKey, CancellationToken token = default)
        => RefreshAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Refresh(CacheKey cacheKey, TimeSpan? expiration, CancellationToken token = default)
        => RefreshAsync(cacheKey, expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Refresh(CacheKey cacheKey, DateTimeOffset? expiration, CancellationToken token = default)
        => RefreshAsync(cacheKey, expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Contains(CacheKey cacheKey, CancellationToken token = default)
        => ContainsAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    TimeSpan? TimeToLive(CacheKey cacheKey, CancellationToken token = default)
        => TimeToLiveAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    DateTimeOffset? ExpireTime(CacheKey cacheKey, CancellationToken token = default)
        => ExpireTimeAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();
}
