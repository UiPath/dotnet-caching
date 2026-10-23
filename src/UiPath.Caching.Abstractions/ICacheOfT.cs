#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;
public interface ICache<T>
{
    string Name { get; }

    [OverloadResolutionPriority(1)]
    ValueTask<T?> GetAsync(CacheKey cacheKey, CancellationToken token = default);


    ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync(CacheKey[] cacheKeys, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetAsync(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetAsync(cacheKey, token)
            : GetAsync(new CacheKey(cacheKey), token);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default);

    ValueTask<T?> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CancellationToken token = default) where TState : notnull;

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CancellationToken token = default) where TState : notnull;

    ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState>(KeyValuePair<CacheKey, TState>[] entries, Func<TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CancellationToken token = default) where TState : notnull;

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default) =>
        this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default) =>
        this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default) =>
        this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default) =>
        this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default) =>
        this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default) =>
        this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, token);

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IStatefulCache<T> stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, token)
            : GetOrAddAsync(cacheKey, StatefulCacheExtensions.Bind(state, generator), token);
    }

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IStatefulCache<T> stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : GetOrAddAsync(cacheKey, StatefulCacheExtensions.Bind(state, generator), expiration, token);
    }

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IStatefulCache<T> stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : GetOrAddAsync(cacheKey, StatefulCacheExtensions.Bind(state, generator), expiration, token);
    }

    /// <inheritdoc cref="IBatchArgumentCache.GetOrAddAsync{T, TState, TArgument}(KeyValuePair{CacheKey, TState}[], TArgument, Func{TArgument, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IBatchArgumentCache<T> capable
            ? capable.GetOrAddAsync(entries, argument, generator, token)
            : GetOrAddAsync(entries, StatefulCacheExtensions.BindBatch(argument, generator), token);
    }

    /// <inheritdoc cref="IBatchArgumentCache.GetOrAddAsync{T, TState, TArgument}(KeyValuePair{CacheKey, TState}[], TArgument, Func{TArgument, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, TimeSpan expiration, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IBatchArgumentCache<T> capable
            ? capable.GetOrAddAsync(entries, argument, generator, expiration, token)
            : GetOrAddAsync(entries, StatefulCacheExtensions.BindBatch(argument, generator), expiration, token);
    }

    /// <inheritdoc cref="IBatchArgumentCache.GetOrAddAsync{T, TState, TArgument}(KeyValuePair{CacheKey, TState}[], TArgument, Func{TArgument, TState[], CancellationToken, Task{KeyValuePair{TState, T}[]}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<KeyValuePair<TState, T?>[]> GetOrAddAsync<TState, TArgument>(KeyValuePair<CacheKey, TState>[] entries, TArgument argument, Func<TArgument, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator, DateTimeOffset expiration, CancellationToken token = default)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IBatchArgumentCache<T> capable
            ? capable.GetOrAddAsync(entries, argument, generator, expiration, token)
            : GetOrAddAsync(entries, StatefulCacheExtensions.BindBatch(argument, generator), expiration, token);
    }

    ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync(CacheKey[] cacheKeys, CancellationToken token = default);

    /// <inheritdoc cref="ICache.RemoveAsync{T}(Span{char}, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RemoveAsync(Span<char> cacheKey, CancellationToken token = default) =>
        RemoveAsync(new CacheKey(cacheKey), token);

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
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync(Span<char> cacheKey, T? value, CancellationToken token = default) =>
        this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, value, token)
            : SetAsync(new CacheKey(cacheKey), value, token);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync(Span<char> cacheKey, T? value, TimeSpan expiration, CancellationToken token = default) =>
        this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, value, expiration, token)
            : SetAsync(new CacheKey(cacheKey), value, expiration, token);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync(Span<char> cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default) =>
        this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, value, expiration, token)
            : SetAsync(new CacheKey(cacheKey), value, expiration, token);

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
    /// <param name="expiration">Lifetime of the entry if it is created.</param>
    ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync(CacheKey, T, TimeSpan, CancellationToken)"/>
    ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> ContainsAsync(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.ContainsAsync(cacheKey, token)
            : ContainsAsync(new CacheKey(cacheKey), token);

    ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ICache.GetOrAddWithExpirationAsync{T}(CacheKey, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<T?> GetOrAddWithExpirationAsync(CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default) =>
        this is IGeneratedExpirationCache<T> capable
            ? capable.GetOrAddWithExpirationAsync(cacheKey, generator, token)
            : GeneratedExpirationExtensions.ReadThenSetAsync(this, cacheKey, generator, token);

    /// <inheritdoc cref="IStatefulGeneratedExpirationCache.GetOrAddWithExpirationAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<T?> GetOrAddWithExpirationAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IStatefulGeneratedExpirationCache<T> stateful
            ? stateful.GetOrAddWithExpirationAsync(cacheKey, state, generator, token)
            : GetOrAddWithExpirationAsync(cacheKey, StatefulCacheExtensions.BindGenerated(state, generator), token);
    }

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddWithExpirationAsync{T}(Span{char}, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddWithExpirationAsync(Span<char> cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default) =>
        this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddWithExpirationAsync(cacheKey, generator, token)
            : GetOrAddWithExpirationAsync(new CacheKey(cacheKey), generator, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddWithExpirationAsync{T}(Span{char}, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetOrAddWithExpirationAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is ISpanKeyCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddWithExpirationAsync(cacheKey, state, generator, token)
            : GetOrAddWithExpirationAsync(new CacheKey(cacheKey), state, generator, token);
    }
}
