#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface IHashCache<T>
{
    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetItemAsync(ref CacheKeyHandler cacheKey, string field, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return GetItemAsync(cacheKey.ToCacheKey(), field, token);
        }

        try
        {
            return spanKeyCache.GetItemAsync(cacheKey.Written, field, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    [OverloadResolutionPriority(1)]
    ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetAsync(ref CacheKeyHandler cacheKey, string[] fields, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return GetAsync(cacheKey.ToCacheKey(), fields, token);
        }

        try
        {
            return spanKeyCache.GetAsync(cacheKey.Written, fields, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return GetAsync(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.GetAsync(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return GetCacheEntryAsync(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.GetCacheEntryAsync(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> ContainsAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return ContainsAsync(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.ContainsAsync(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<TimeSpan?> TimeToLiveAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return TimeToLiveAsync(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.TimeToLiveAsync(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<DateTimeOffset?> ExpireTimeAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return ExpireTimeAsync(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.ExpireTimeAsync(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<IDictionary<string, string?>?> GetMetadataAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, string?>?> GetMetadataAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return GetMetadataAsync(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.GetMetadataAsync(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }
}
