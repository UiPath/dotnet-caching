namespace UiPath.Caching;

/// <summary>An <see cref="IHashCache"/> whose <c>GetOrAddAsync</c> hands the generator its state, so a static lambda needs no closure; the state members of <see cref="IHashCache"/> prefer it.</summary>
/// <remarks>Apart from <see cref="IHashCache"/>, so an implementation outside the library and a mock keep working through the closure overloads. Implemented by the library's in-memory and multilayer hash caches.</remarks>
public interface IStatefulHashCache
{
    /// <summary>Reads, and on a miss runs <paramref name="generator"/> with <paramref name="state"/>; a hit allocates no closure.</summary>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default);
}
