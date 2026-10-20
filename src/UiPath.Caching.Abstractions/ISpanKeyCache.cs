namespace UiPath.Caching;

/// <summary>An <see cref="ICache"/> that reads by the key's text without building a <see cref="CacheKey"/>; the <c>Span&lt;char&gt;</c> members of <see cref="ICache"/> prefer it.</summary>
/// <remarks>Apart from <see cref="ICache"/>, so a mock never has to proxy a <c>Span&lt;char&gt;</c> parameter. Implemented by the library's in-memory and multilayer caches.</remarks>
public interface ISpanKeyCache
{
    /// <summary>Reads by the key's text, normalized as <c>new CacheKey(text)</c> normalizes it, so a local hit need not allocate.</summary>
    ValueTask<T?> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <summary>Reads by the key's text as <see cref="GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/> does; a miss builds the key and takes the <see cref="CacheKey"/> path.</summary>
    ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <summary>Reads by the key's text as <see cref="GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/> does, handing the generator <paramref name="state"/> so a static lambda needs no closure.</summary>
    ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddAsync{T, TState}(Span{char}, TState, Func{TState, CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<bool> ContainsAsync<T>(Span<char> cacheKey, CancellationToken token = default);

    /// <summary>Reads by the key's text as <see cref="GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/> does; a miss builds the key and runs <paramref name="generator"/> as <see cref="IGeneratedExpirationCache.GetOrAddWithExpirationAsync{T}(CacheKey, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/> does, without reading the local tier again.</summary>
    ValueTask<T?> GetOrAddWithExpirationAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="GetOrAddWithExpirationAsync{T}(Span{char}, Func{CancellationToken, Task{GeneratedValue{T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetOrAddWithExpirationAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <summary>Writes by the key's text, normalized as <c>new CacheKey(text)</c> normalizes it; a stored entry needs the key as a string, so an implementation builds it once.</summary>
    ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    ValueTask<bool> SetAsync<T>(Span<char> cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);
}
