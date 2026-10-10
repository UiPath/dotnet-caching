namespace UiPath.Caching;

/// <summary>Reads with a generator that takes its state: through <see cref="IStatefulCache"/> when the cache implements it, otherwise by closing over the state.</summary>
public static class StatefulCacheExtensions
{
    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return cache is IStatefulCache stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, policy, token)
            : cache.GetOrAddAsync(cacheKey, Bind(state, generator), policy, token);
    }

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return cache is IStatefulCache stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, expiration, policy, token)
            : cache.GetOrAddAsync(cacheKey, Bind(state, generator), expiration, policy, token);
    }

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return cache is IStatefulCache stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, expiration, policy, token)
            : cache.GetOrAddAsync(cacheKey, Bind(state, generator), expiration, policy, token);
    }

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default) =>
        cache.GetOrAddAsync(cacheKey, state, generator, (CachePolicy?)null, token);

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default) =>
        cache.GetOrAddAsync(cacheKey, state, generator, expiration, null, token);

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default) =>
        cache.GetOrAddAsync(cacheKey, state, generator, expiration, null, token);

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache<T> cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return cache is IStatefulCache<T> stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, token)
            : cache.GetOrAddAsync(cacheKey, Bind(state, generator), token);
    }

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache<T> cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return cache is IStatefulCache<T> stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : cache.GetOrAddAsync(cacheKey, Bind(state, generator), expiration, token);
    }

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache<T> cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return cache is IStatefulCache<T> stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : cache.GetOrAddAsync(cacheKey, Bind(state, generator), expiration, token);
    }

    // Apart from the callers, so only the fallback builds the closure and a stateful cache's hit allocates none.
    private static Func<CancellationToken, Task<T?>> Bind<T, TState>(TState state, Func<TState, CancellationToken, Task<T?>> generator) =>
        token => generator(state, token);
}
