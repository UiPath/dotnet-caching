namespace UiPath.Caching;

/// <summary>An <see cref="ICache"/> that reads by the key's text without building a <see cref="CacheKey"/>; <see cref="SpanKeyExtensions"/> prefers it.</summary>
/// <remarks>Apart from <see cref="ICache"/>, so a mock of <see cref="ICache"/> never has to proxy a <c>Span&lt;char&gt;</c> parameter.</remarks>
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

    /// <inheritdoc cref="GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<bool> ContainsAsync<T>(Span<char> cacheKey, CancellationToken token = default);
}
