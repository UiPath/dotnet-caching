namespace UiPath.Caching;

/// <summary>An <see cref="ICache{T}"/> whose <c>GetOrAddAsync</c> hands the generator its state, so a static lambda needs no closure; <see cref="StatefulCacheExtensions"/> prefers it.</summary>
public interface IStatefulCache<T>
{
    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default);

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="IStatefulCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default);
}
