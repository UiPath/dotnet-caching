#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface IHashCache
{
    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetItemAsync<T>(ref CacheKeyHandler cacheKey, string field, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetItemAsync<T>(cacheKey.ToCacheKey(), field, policy, token);
        }

        try
        {
            return spanKeyCache.GetItemAsync<T>(cacheKey.Written, field, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    [OverloadResolutionPriority(1)]
    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetAsync<T>(ref CacheKeyHandler cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetAsync<T>(cacheKey.ToCacheKey(), fields, policy, token);
        }

        try
        {
            return spanKeyCache.GetAsync<T>(cacheKey.Written, fields, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetAsync<T>(ref CacheKeyHandler cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
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

    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(ref CacheKeyHandler cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
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

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> ContainsAsync<T>(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
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
        if (this is not ISpanKeyHashCache spanKeyCache)
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
        if (this is not ISpanKeyHashCache spanKeyCache)
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

    ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetMetadataAsync<T>(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.GetMetadataAsync<T>(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }
}
