#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;
public interface ICache<T>
{
    string Name { get; }

    [OverloadResolutionPriority(1)]
    ValueTask<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default);


    ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync(CacheKey[] cacheKeys, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
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

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState, TArgument}(KeyValuePair{CacheKey, TState}[], TArgument, Func{TArgument, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CancellationToken token = default)
        where TState : notnull;

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState, TArgument}(KeyValuePair{CacheKey, TState}[], TArgument, Func{TArgument, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CancellationToken token = default)
        where TState : notnull;

    /// <inheritdoc cref="ICache.GetOrAddAsync{T, TState, TArgument}(KeyValuePair{CacheKey, TState}[], TArgument, Func{TArgument, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CancellationToken token = default)
        where TState : notnull;

    ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync(CacheKey[] cacheKeys, CancellationToken token = default);

    /// <inheritdoc cref="ICache.RemoveAsync{T}(CacheKey, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RemoveAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default) =>
        RemoveAsync(cacheKey.ToCacheKey(), token);

    /// <inheritdoc cref="ISpanKeyCache.RemoveManyAsync{T}(IEnumerable{CacheKey}, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> RemoveManyAsync(IEnumerable<CacheKey> cacheKeys, CancellationToken token = default) =>
        this is ISpanKeyCache<T> multi
            ? multi.RemoveManyAsync(cacheKeys, token)
            : MultiKeyFallback.RunAsync(cacheKeys, (Cache: this, Token: token), static k => k, static (s, k) => s.Cache.RemoveAsync(k, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, CancellationToken token = default) =>
        this is ISpanKeyCache<T> multi
            ? multi.SetManyAsync(keyValues, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync(kv.Key, kv.Value, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, TimeSpan expiration, CancellationToken token = default) =>
        this is ISpanKeyCache<T> multi
            ? multi.SetManyAsync(keyValues, expiration, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Expiration: expiration, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync(kv.Key, kv.Value, s.Expiration, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, DateTimeOffset expiration, CancellationToken token = default) =>
        this is ISpanKeyCache<T> multi
            ? multi.SetManyAsync(keyValues, expiration, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Expiration: expiration, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync(kv.Key, kv.Value, s.Expiration, s.Token));

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync(ref CacheKeyHandler cacheKey, T? value, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), value, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, value, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync(ref CacheKeyHandler cacheKey, T? value, TimeSpan expiration, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), value, expiration, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, value, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync(ref CacheKeyHandler cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), value, expiration, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, value, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token = default);

    ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CancellationToken token = default);

    /// <summary>
    /// Typed façade over
    /// <see cref="ICache.TryAddAsync{T}(CacheKey, T, TimeSpan, CachePolicy, CancellationToken)"/>;
    /// see that member for the contract.
    /// </summary>
    ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync(CacheKey, T, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> TryAddAsync(ref CacheKeyHandler cacheKey, T? value, CancellationToken token = default) =>
        TryAddAsync(cacheKey.ToCacheKey(), value, token);

    /// <inheritdoc cref="TryAddAsync(CacheKey, T, CancellationToken)"/>
    /// <param name="expiration">Lifetime of the entry if it is created.</param>
    ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync(CacheKey, T, TimeSpan, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> TryAddAsync(ref CacheKeyHandler cacheKey, T? value, TimeSpan expiration, CancellationToken token = default) =>
        TryAddAsync(cacheKey.ToCacheKey(), value, expiration, token);

    /// <inheritdoc cref="TryAddAsync(CacheKey, T, TimeSpan, CancellationToken)"/>
    ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync(CacheKey, T, DateTimeOffset, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> TryAddAsync(ref CacheKeyHandler cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default) =>
        TryAddAsync(cacheKey.ToCacheKey(), value, expiration, token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default) =>
        RefreshAsync(cacheKey.ToCacheKey(), token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, TimeSpan, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync(ref CacheKeyHandler cacheKey, TimeSpan expiration, CancellationToken token = default) =>
        RefreshAsync(cacheKey.ToCacheKey(), expiration, token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, DateTimeOffset, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync(ref CacheKeyHandler cacheKey, DateTimeOffset expiration, CancellationToken token = default) =>
        RefreshAsync(cacheKey.ToCacheKey(), expiration, token);

    ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> ContainsAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
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
        if (this is not ISpanKeyCache<T> spanKeyCache)
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
        if (this is not ISpanKeyCache<T> spanKeyCache)
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
