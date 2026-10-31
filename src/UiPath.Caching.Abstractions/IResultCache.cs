namespace UiPath.Caching;

/// <summary>What a cache reports through <c>GetOrAddResultAsync</c> without the wrapper the default implementation builds; the library's caches implement it, any other <see cref="ICache"/> takes the default.</summary>
internal interface IResultCache
{
    ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);
}
