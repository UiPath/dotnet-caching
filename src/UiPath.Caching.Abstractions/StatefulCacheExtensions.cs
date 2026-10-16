namespace UiPath.Caching;

/// <summary>The policy-less state overloads of <see cref="ICache"/>.</summary>
/// <remarks>For a hot path; the closure overloads are the default shape (see <c>docs/how-to/choosing-a-get-or-add-overload.md</c>).</remarks>
public static class StatefulCacheExtensions
{
    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default) =>
        cache.GetOrAddAsync(cacheKey, state, generator, (CachePolicy?)null, token);

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default) =>
        cache.GetOrAddAsync(cacheKey, state, generator, expiration, null, token);

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddAsync<T, TState>(this ICache cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default) =>
        cache.GetOrAddAsync(cacheKey, state, generator, expiration, null, token);

    // Apart from the callers, so only the fallback builds the closure and a stateful cache's hit allocates none.
    internal static Func<CancellationToken, Task<T?>> Bind<T, TState>(TState state, Func<TState, CancellationToken, Task<T?>> generator) =>
        token => generator(state, token);
}
