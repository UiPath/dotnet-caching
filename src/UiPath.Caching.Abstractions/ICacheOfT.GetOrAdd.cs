#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface ICache<T>
{
    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CancellationToken token = default) where TState : notnull;

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CancellationToken token = default) where TState : notnull;

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CancellationToken token = default) where TState : notnull;

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetOrAddAsync(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
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
    sealed ValueTask<T?> GetOrAddAsync(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
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
    sealed ValueTask<T?> GetOrAddAsync(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
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

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetOrAddAsync<TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
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

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetOrAddAsync<TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
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

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetOrAddAsync<TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
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

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default);

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState, TArg}(KeyValuePair{CacheKey, TState}[], TArg, Func{TArg, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CancellationToken token = default)
        where TState : notnull;

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState, TArg}(KeyValuePair{CacheKey, TState}[], TArg, Func{TArg, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CancellationToken token = default)
        where TState : notnull;

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState, TArg}(KeyValuePair{CacheKey, TState}[], TArg, Func{TArg, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CancellationToken token = default)
        where TState : notnull;

    /// <inheritdoc cref="ICache.GetOrAddWithExpirationAsync{T}(CacheKey, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddWithExpirationAsync(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default);

    /// <inheritdoc cref="ICache.GetOrAddWithExpirationAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddWithExpirationAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddWithExpirationAsync{T}(Span{char}, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetOrAddWithExpirationAsync(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
        {
            return GetOrAddWithExpirationAsync(cacheKey.ToCacheKey(), generator, token);
        }

        try
        {
            return spanKeyCache.GetOrAddWithExpirationAsync(cacheKey.Written, generator, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddWithExpirationAsync{T}(Span{char}, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetOrAddWithExpirationAsync<TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyCache<T> spanKeyCache)
        {
            return GetOrAddWithExpirationAsync(cacheKey.ToCacheKey(), state, generator, token);
        }

        try
        {
            return spanKeyCache.GetOrAddWithExpirationAsync(cacheKey.Written, state, generator, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <summary>Reads, and on a miss runs <paramref name="generator"/> as <c>GetOrAddAsync</c> does; reports whether this call's generator produced the value and when the entry expires.</summary>
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        if (this is IResultCache<T> source)
        {
            return source.GetOrAddResultAsync(cacheKey, generator, token);
        }

        var recorder = GeneratorRecorder<T>.For(generator);
        return recorder.ResultOf(GetOrAddAsync(cacheKey, recorder.Generator, token));
    }

    /// <inheritdoc cref="ICache.GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        if (this is IResultCache<T> source)
        {
            return source.GetOrAddResultAsync(cacheKey, generator, expiration, token);
        }

        var recorder = GeneratorRecorder<T>.For(generator);
        return recorder.ResultOf(GetOrAddAsync(cacheKey, recorder.Generator, expiration, token));
    }

    /// <inheritdoc cref="ICache.GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        if (this is IResultCache<T> source)
        {
            return source.GetOrAddResultAsync(cacheKey, generator, expiration, token);
        }

        var recorder = GeneratorRecorder<T>.For(generator);
        return recorder.ResultOf(GetOrAddAsync(cacheKey, recorder.Generator, expiration, token), expiration);
    }

    /// <inheritdoc cref="ICache.GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        if (this is IResultCache<T> source)
        {
            return source.GetOrAddResultAsync(cacheKey, state, generator, token);
        }

        var recorder = GeneratorRecorder<T>.For(state, generator);
        return recorder.ResultOf(GetOrAddAsync(cacheKey, recorder.Generator, token));
    }

    /// <inheritdoc cref="ICache.GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        if (this is IResultCache<T> source)
        {
            return source.GetOrAddResultAsync(cacheKey, state, generator, expiration, token);
        }

        var recorder = GeneratorRecorder<T>.For(state, generator);
        return recorder.ResultOf(GetOrAddAsync(cacheKey, recorder.Generator, expiration, token));
    }

    /// <inheritdoc cref="ICache.GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        if (this is IResultCache<T> source)
        {
            return source.GetOrAddResultAsync(cacheKey, state, generator, expiration, token);
        }

        var recorder = GeneratorRecorder<T>.For(state, generator);
        return recorder.ResultOf(GetOrAddAsync(cacheKey, recorder.Generator, expiration, token), expiration);
    }

    /// <inheritdoc cref="ICache.GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        if (this is not IResultCache<T> results)
        {
            return GetOrAddResultAsync(cacheKey.ToCacheKey(), generator, token);
        }

        try
        {
            return results.GetOrAddResultAsync(cacheKey.Written, generator, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ICache.GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        if (this is not IResultCache<T> results)
        {
            return GetOrAddResultAsync(cacheKey.ToCacheKey(), generator, expiration, token);
        }

        try
        {
            return results.GetOrAddResultAsync(cacheKey.Written, generator, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ICache.GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        if (this is not IResultCache<T> results)
        {
            return GetOrAddResultAsync(cacheKey.ToCacheKey(), generator, expiration, token);
        }

        try
        {
            return results.GetOrAddResultAsync(cacheKey.Written, generator, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ICache.GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        if (this is not IResultCache<T> results)
        {
            return GetOrAddResultAsync(cacheKey.ToCacheKey(), state, generator, token);
        }

        try
        {
            return results.GetOrAddResultAsync(cacheKey.Written, state, generator, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ICache.GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        if (this is not IResultCache<T> results)
        {
            return GetOrAddResultAsync(cacheKey.ToCacheKey(), state, generator, expiration, token);
        }

        try
        {
            return results.GetOrAddResultAsync(cacheKey.Written, state, generator, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ICache.GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        if (this is not IResultCache<T> results)
        {
            return GetOrAddResultAsync(cacheKey.ToCacheKey(), state, generator, expiration, token);
        }

        try
        {
            return results.GetOrAddResultAsync(cacheKey.Written, state, generator, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }
}
