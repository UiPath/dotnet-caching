#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface ICache
{
    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull;

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull;

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull;

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetOrAddAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
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
    sealed ValueTask<T?> GetOrAddAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
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
    sealed ValueTask<T?> GetOrAddAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
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

    /// <summary>Reads, and on a miss runs <paramref name="generator"/>, with the key written as an interpolated string: <c>$"{tenant}:{id}"</c> builds no key object and allocates nothing for a short key.</summary>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetOrAddAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
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

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetOrAddAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
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

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetOrAddAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
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

    /// <summary>Reads, and on a miss runs <paramref name="generator"/> with <paramref name="state"/> as <see cref="GetOrAddAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/> runs its generator; a hit allocates no closure.</summary>
    ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <summary>Reads the entries, and for the keys that missed runs <paramref name="generator"/> once with <paramref name="argument"/> and their states, as the batch overload does; the argument lets a static lambda need no closure.</summary>
    /// <param name="entries">The keys, each with its own state, as in the batch overload.</param>
    /// <param name="argument">One value for the whole call, passed to <paramref name="generator"/> before the states; not a per-entry state.</param>
    /// <param name="generator">Receives <paramref name="argument"/> and the states of the keys that missed.</param>
    /// <param name="policy">The policy, as in the batch overload.</param>
    /// <param name="token">The cancellation token.</param>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull;

    /// <inheritdoc cref="GetOrAddAsync{T, TState, TArg}(KeyValuePair{CacheKey, TState}[], TArg, Func{TArg, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull;

    /// <inheritdoc cref="GetOrAddAsync{T, TState, TArg}(KeyValuePair{CacheKey, TState}[], TArg, Func{TArg, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<T, TState, TArg>(KeyValuePair<CacheKey, TState>[] entries, TArg argument, Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
        where TState : notnull;

    /// <summary>Reads, and on a miss runs <paramref name="generator"/> as <see cref="GetOrAddAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/> does, storing the value until the expiration it returns; an expiration that has already passed stores nothing.</summary>
    ValueTask<T?> GetOrAddWithExpirationAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <summary>Reads, and on a miss runs <paramref name="generator"/> with <paramref name="state"/>, storing the value until the expiration it returns.</summary>
    ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddWithExpirationAsync{T}(Span{char}, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetOrAddWithExpirationAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
        {
            return GetOrAddWithExpirationAsync(cacheKey.ToCacheKey(), generator, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddWithExpirationAsync(cacheKey.Written, generator, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddWithExpirationAsync{T}(Span{char}, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyCache spanKeyCache)
        {
            return GetOrAddWithExpirationAsync(cacheKey.ToCacheKey(), state, generator, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddWithExpirationAsync(cacheKey.Written, state, generator, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <summary>Reads, and on a miss runs <paramref name="generator"/> as <c>GetOrAddAsync</c> does; reports whether this call's generator produced the value and when the entry expires.</summary>
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is IResultCache source)
        {
            return source.GetOrAddResultAsync(cacheKey, generator, policy, token);
        }

        var recorder = GeneratorRecorder<T>.For(generator);
        return recorder.ResultOf(GetOrAddAsync<T>(cacheKey, recorder.Generator, policy, token));
    }

    /// <inheritdoc cref="GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is IResultCache source)
        {
            return source.GetOrAddResultAsync(cacheKey, generator, expiration, policy, token);
        }

        var recorder = GeneratorRecorder<T>.For(generator);
        return recorder.ResultOf(GetOrAddAsync<T>(cacheKey, recorder.Generator, expiration, policy, token));
    }

    /// <inheritdoc cref="GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is IResultCache source)
        {
            return source.GetOrAddResultAsync(cacheKey, generator, expiration, policy, token);
        }

        var recorder = GeneratorRecorder<T>.For(generator);
        return recorder.ResultOf(GetOrAddAsync<T>(cacheKey, recorder.Generator, expiration, policy, token), expiration);
    }

    /// <inheritdoc cref="GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is IResultCache source)
        {
            return source.GetOrAddResultAsync(cacheKey, state, generator, policy, token);
        }

        var recorder = GeneratorRecorder<T>.For(state, generator);
        return recorder.ResultOf(GetOrAddAsync<T>(cacheKey, recorder.Generator, policy, token));
    }

    /// <inheritdoc cref="GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is IResultCache source)
        {
            return source.GetOrAddResultAsync(cacheKey, state, generator, expiration, policy, token);
        }

        var recorder = GeneratorRecorder<T>.For(state, generator);
        return recorder.ResultOf(GetOrAddAsync<T>(cacheKey, recorder.Generator, expiration, policy, token));
    }

    /// <inheritdoc cref="GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is IResultCache source)
        {
            return source.GetOrAddResultAsync(cacheKey, state, generator, expiration, policy, token);
        }

        var recorder = GeneratorRecorder<T>.For(state, generator);
        return recorder.ResultOf(GetOrAddAsync<T>(cacheKey, recorder.Generator, expiration, policy, token), expiration);
    }

    /// <inheritdoc cref="GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not IResultCache results)
        {
            return GetOrAddResultAsync(cacheKey.ToCacheKey(), generator, policy, token);
        }

        try
        {
            return results.GetOrAddResultAsync(cacheKey.Written, generator, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not IResultCache results)
        {
            return GetOrAddResultAsync(cacheKey.ToCacheKey(), generator, expiration, policy, token);
        }

        try
        {
            return results.GetOrAddResultAsync(cacheKey.Written, generator, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not IResultCache results)
        {
            return GetOrAddResultAsync(cacheKey.ToCacheKey(), generator, expiration, policy, token);
        }

        try
        {
            return results.GetOrAddResultAsync(cacheKey.Written, generator, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not IResultCache results)
        {
            return GetOrAddResultAsync(cacheKey.ToCacheKey(), state, generator, policy, token);
        }

        try
        {
            return results.GetOrAddResultAsync(cacheKey.Written, state, generator, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not IResultCache results)
        {
            return GetOrAddResultAsync(cacheKey.ToCacheKey(), state, generator, expiration, policy, token);
        }

        try
        {
            return results.GetOrAddResultAsync(cacheKey.Written, state, generator, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="GetOrAddResultAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not IResultCache results)
        {
            return GetOrAddResultAsync(cacheKey.ToCacheKey(), state, generator, expiration, policy, token);
        }

        try
        {
            return results.GetOrAddResultAsync(cacheKey.Written, state, generator, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }
}
