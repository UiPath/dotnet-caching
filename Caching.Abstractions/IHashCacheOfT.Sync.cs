namespace UiPath.Platform.Caching;

// Sync forwarders over the async API. Each default interface method blocks on the underlying
// async call via .AsTask().GetAwaiter().GetResult() — use only from sync call sites that can
// tolerate the thread-blocking cost. Excluded from coverage — forwarders with no behavior of
// their own; the async impls are what tests exercise.
public partial interface IHashCache<T>
{
    [ExcludeFromCodeCoverage]
    T? GetItem(CacheKey cacheKey, string field, CancellationToken token = default)
        => GetItemAsync(cacheKey, field, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    IDictionary<string, T?> Get(CacheKey cacheKey, CancellationToken token = default)
        => GetAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    IDictionary<string, T?> Get(CacheKey cacheKey, string[] fields, CancellationToken token = default)
        => GetAsync(cacheKey, fields, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    IDictionary<string, T?> GetOrAdd(CacheKey cacheKey, Func<IDictionary<string, T?>> generator, CancellationToken token = default)
        => GetOrAddAsync(cacheKey, _ => Task.FromResult(generator()), token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    IDictionary<string, T?> GetOrAdd(CacheKey cacheKey, Func<IDictionary<string, T?>> generator, TimeSpan? expiration, CancellationToken token = default)
        => GetOrAddAsync(cacheKey, _ => Task.FromResult(generator()), expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    IDictionary<string, T?> GetOrAdd(CacheKey cacheKey, Func<IDictionary<string, T?>> generator, DateTimeOffset? expiration, CancellationToken token = default)
        => GetOrAddAsync(cacheKey, _ => Task.FromResult(generator()), expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    ICacheEntry<IDictionary<string, T?>> GetCacheEntry(CacheKey cacheKey, CancellationToken token = default)
        => GetCacheEntryAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Set(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default)
        => SetAsync(cacheKey, values, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Set(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan? expiration, CancellationToken token = default)
        => SetAsync(cacheKey, values, expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Set(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset? expiration, CancellationToken token = default)
        => SetAsync(cacheKey, values, expiration, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Set(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default)
        => SetAsync(cacheKey, values, options, token).AsTask().GetAwaiter().GetResult();

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
    bool Refresh(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default)
        => RefreshAsync(cacheKey, options, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Remove(CacheKey cacheKey, CancellationToken token = default)
        => RemoveAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool Contains(CacheKey cacheKey, CancellationToken token = default)
        => ContainsAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    TimeSpan? TimeToLive(CacheKey cacheKey, CancellationToken token = default)
        => TimeToLiveAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    DateTimeOffset? ExpireTime(CacheKey cacheKey, CancellationToken token = default)
        => ExpireTimeAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    IDictionary<string, string?>? GetMetadata(CacheKey cacheKey, CancellationToken token = default)
        => GetMetadataAsync(cacheKey, token).AsTask().GetAwaiter().GetResult();

    [ExcludeFromCodeCoverage]
    bool SetMetadata(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default)
        => SetMetadataAsync(cacheKey, metadata, token).AsTask().GetAwaiter().GetResult();
}
