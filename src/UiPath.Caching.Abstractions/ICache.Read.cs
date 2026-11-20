#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface ICache
{
    ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetAsync<T>(ref CacheKeyHandler cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
        {
            return GetAsync<T>(cacheKey.ToCacheKey(), policy, token);
        }

        try
        {
            return spanKeyCache.GetAsync<T>(cacheKey.Written, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(ref CacheKeyHandler cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
        {
            return GetCacheEntryAsync<T>(cacheKey.ToCacheKey(), policy, token);
        }

        try
        {
            return spanKeyCache.GetCacheEntryAsync<T>(cacheKey.Written, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> ContainsAsync<T>(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
        {
            return ContainsAsync<T>(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.ContainsAsync<T>(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<TimeSpan?> TimeToLiveAsync<T>(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
        {
            return TimeToLiveAsync<T>(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.TimeToLiveAsync<T>(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
        {
            return ExpireTimeAsync<T>(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.ExpireTimeAsync<T>(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }
}
