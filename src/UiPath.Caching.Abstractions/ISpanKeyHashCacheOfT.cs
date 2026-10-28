#pragma warning disable S3060 // by design: the default body serves a cache through its CacheKey members
namespace UiPath.Caching;

/// <summary>An <see cref="IHashCache{T}"/> that reads by the key's text without building a <see cref="CacheKey"/>; the <c>Span&lt;char&gt;</c> members of <see cref="IHashCache{T}"/> prefer it, and its batch and field-removal members too.</summary>
public interface ISpanKeyHashCache<T>
{
    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<T?> GetItemAsync(Span<char> cacheKey, string field, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetAsync(Span<char> cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetAsync(Span<char> cacheKey, string[] fields, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(Span<char> cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<bool> ContainsAsync(Span<char> cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<TimeSpan?> TimeToLiveAsync(Span<char> cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<DateTimeOffset?> ExpireTimeAsync(Span<char> cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, string?>?> GetMetadataAsync(Span<char> cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    ValueTask<bool> SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    ValueTask<bool> SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    ValueTask<bool> SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    ValueTask<bool> SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyHashCache.RemoveFieldsAsync{T}(CacheKey, string[], CancellationToken)"/>
    ValueTask<bool> RemoveFieldsAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        throw new NotSupportedException($"{GetType().Name} does not remove single fields of a hash.");

    /// <inheritdoc cref="ISpanKeyCache.RemoveManyAsync{T}(IEnumerable{CacheKey}, CancellationToken)"/>
    ValueTask<IReadOnlyList<CacheKey>> RemoveManyAsync(IEnumerable<CacheKey> cacheKeys, CancellationToken token = default) =>
        this is IHashCache<T> cache
            ? MultiKeyFallback.RunAsync(cacheKeys, (Cache: cache, Token: token), static k => k, static (s, k) => s.Cache.RemoveAsync(k, s.Token))
            : throw new NotSupportedException($"{GetType().Name} is not a cache.");

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, IDictionary<string, T?>>> keyValues, HashCacheEntryOptions options, CancellationToken token = default) =>
        this is IHashCache<T> cache
            ? MultiKeyFallback.RunAsync(keyValues, (Cache: cache, Options: options, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync(kv.Key, kv.Value, s.Options, s.Token))
            : throw new NotSupportedException($"{GetType().Name} is not a cache.");
}
