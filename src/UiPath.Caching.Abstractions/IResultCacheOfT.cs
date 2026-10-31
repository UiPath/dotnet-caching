namespace UiPath.Caching;

/// <summary>The typed counterpart of <see cref="IResultCache"/>, implemented by the library's <see cref="ICache{T}"/>.</summary>
internal interface IResultCache<T>
{
    ValueTask<CacheResult<T>> GetOrAddResultAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default);

    ValueTask<CacheResult<T>> GetOrAddResultAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default);
}
