namespace UiPath.Caching;

/// <summary>The policy-less <c>GetOrAddWithExpirationAsync</c> of <see cref="ICache"/>.</summary>
public static class GeneratedExpirationExtensions
{
    /// <inheritdoc cref="ICache.GetOrAddWithExpirationAsync{T}(CacheKey, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddWithExpirationAsync<T>(this ICache cache, CacheKey cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default) =>
        cache.GetOrAddWithExpirationAsync(cacheKey, generator, (CachePolicy?)null, token);

    /// <inheritdoc cref="ICache.GetOrAddWithExpirationAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    public static ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(this ICache cache, CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default) =>
        cache.GetOrAddWithExpirationAsync(cacheKey, state, generator, (CachePolicy?)null, token);
}
