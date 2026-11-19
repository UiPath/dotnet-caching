#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface IHashCache<T>
{
    ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), generator, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, generator, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), generator, expiration, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, generator, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), generator, expiration, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, generator, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), state, generator, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, state, generator, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), state, generator, expiration, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, state, generator, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), state, generator, expiration, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, state, generator, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }
}
