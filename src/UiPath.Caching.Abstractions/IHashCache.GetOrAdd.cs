#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface IHashCache
{
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), generator, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, generator, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), generator, expiration, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, generator, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), generator, expiration, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, generator, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), generator, expiration, setOption, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, generator, expiration, setOption, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <summary>Reads, and on a miss runs <paramref name="generator"/> with <paramref name="state"/>; a hit allocates no closure.</summary>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), state, generator, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, state, generator, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), state, generator, expiration, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, state, generator, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), state, generator, expiration, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, state, generator, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), state, generator, expiration, setOption, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, state, generator, expiration, setOption, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }
}
