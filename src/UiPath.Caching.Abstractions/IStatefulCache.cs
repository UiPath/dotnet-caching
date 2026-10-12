namespace UiPath.Caching;

/// <summary>An <see cref="ICache"/> whose <c>GetOrAddAsync</c> hands the generator its state, so a static lambda needs no closure; <see cref="StatefulCacheExtensions"/> prefers it.</summary>
/// <remarks>Apart from <see cref="ICache"/>, so an implementation outside the library and a mock of <see cref="ICache"/> keep working; the extensions fall back to the closure overloads.</remarks>
public interface IStatefulCache
{
    /// <summary>Reads, and on a miss runs <paramref name="generator"/> with <paramref name="state"/> as <see cref="ICache.GetOrAddAsync{T}(CacheKey, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/> runs its generator; a hit allocates no closure.</summary>
    ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);
}
