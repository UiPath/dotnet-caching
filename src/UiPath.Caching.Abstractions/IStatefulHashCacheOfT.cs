namespace UiPath.Caching;

/// <summary>An <see cref="IHashCache{T}"/> whose <c>GetOrAddAsync</c> hands the generator its state, so a static lambda needs no closure; the state members of <see cref="IHashCache{T}"/> prefer it.</summary>
public interface IStatefulHashCache<T>
{
    /// <inheritdoc cref="IStatefulHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    /// <inheritdoc cref="IStatefulHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="IStatefulHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default);
}
