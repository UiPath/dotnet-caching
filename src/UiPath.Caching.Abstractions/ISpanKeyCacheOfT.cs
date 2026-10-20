namespace UiPath.Caching;

/// <summary>An <see cref="ICache{T}"/> that reads by the key's text without building a <see cref="CacheKey"/>; the <c>Span&lt;char&gt;</c> members of <see cref="ICache{T}"/> prefer it.</summary>
public interface ISpanKeyCache<T>
{
    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetAsync(Span<char> cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<bool> ContainsAsync(Span<char> cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddWithExpirationAsync{T}(Span{char}, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddWithExpirationAsync(Span<char> cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddWithExpirationAsync{T}(Span{char}, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddWithExpirationAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CancellationToken token = default);
}
