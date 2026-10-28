#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public interface IHashCache<T>
{
    string Name { get; }

    [OverloadResolutionPriority(1)]
    ValueTask<T?> GetItemAsync(CacheKey cacheKey, string field, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetItemAsync(Span<char> cacheKey, string field, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetItemAsync(cacheKey, field, token)
            : GetItemAsync(new CacheKey(cacheKey), field, token);


    [OverloadResolutionPriority(1)]
    ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, CancellationToken token = default);


    ValueTask<IDictionary<string, T?>> GetAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetAsync(Span<char> cacheKey, string[] fields, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetAsync(cacheKey, fields, token)
            : GetAsync(new CacheKey(cacheKey), fields, token);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetAsync(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetAsync(cacheKey, token)
            : GetAsync(new CacheKey(cacheKey), token);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, token);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, token);
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, token);
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, token);
    }

    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetCacheEntryAsync(cacheKey, token)
            : GetCacheEntryAsync(new CacheKey(cacheKey), token);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, values, token)
            : SetAsync(new CacheKey(cacheKey), values, token);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, values, expiration, token)
            : SetAsync(new CacheKey(cacheKey), values, expiration, token);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, values, expiration, token)
            : SetAsync(new CacheKey(cacheKey), values, expiration, token);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync(Span<char> cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, values, options, token)
            : SetAsync(new CacheKey(cacheKey), values, options, token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RefreshAsync(Span<char> cacheKey, CancellationToken token = default) =>
        RefreshAsync(new CacheKey(cacheKey), token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, TimeSpan, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RefreshAsync(Span<char> cacheKey, TimeSpan expiration, CancellationToken token = default) =>
        RefreshAsync(new CacheKey(cacheKey), expiration, token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, DateTimeOffset, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RefreshAsync(Span<char> cacheKey, DateTimeOffset expiration, CancellationToken token = default) =>
        RefreshAsync(new CacheKey(cacheKey), expiration, token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, HashCacheEntryOptions, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RefreshAsync(Span<char> cacheKey, HashCacheEntryOptions options, CancellationToken token = default) =>
        RefreshAsync(new CacheKey(cacheKey), options, token);

    ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ICache.RemoveAsync{T}(Span{char}, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RemoveAsync(Span<char> cacheKey, CancellationToken token = default) =>
        RemoveAsync(new CacheKey(cacheKey), token);

    /// <inheritdoc cref="ISpanKeyHashCache.RemoveFieldsAsync{T}(CacheKey, string[], CancellationToken)"/>
    /// <exception cref="NotSupportedException">The cache does not implement <see cref="ISpanKeyHashCache{T}"/>.</exception>
    sealed ValueTask<bool> RemoveFieldsAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> removal
            ? removal.RemoveFieldsAsync(cacheKey, fields, token)
            : throw new NotSupportedException($"{GetType().Name} does not remove single fields of a hash.");

    /// <inheritdoc cref="RemoveFieldsAsync(CacheKey, string[], CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RemoveFieldsAsync(Span<char> cacheKey, string[] fields, CancellationToken token = default) =>
        RemoveFieldsAsync(new CacheKey(cacheKey), fields, token);

    /// <inheritdoc cref="ISpanKeyCache.RemoveManyAsync{T}(IEnumerable{CacheKey}, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> RemoveManyAsync(IEnumerable<CacheKey> cacheKeys, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> multi
            ? multi.RemoveManyAsync(cacheKeys, token)
            : MultiKeyFallback.RunAsync(cacheKeys, (Cache: this, Token: token), static k => k, static (s, k) => s.Cache.RemoveAsync(k, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, IDictionary<string, T?>>> keyValues, HashCacheEntryOptions options, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> multi
            ? multi.SetManyAsync(keyValues, options, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Options: options, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync(kv.Key, kv.Value, s.Options, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, IDictionary<string, T?>>> keyValues, CancellationToken token = default) =>
        SetManyAsync(keyValues, new HashCacheEntryOptions(SetOption: HashCacheSetOption.KeyReplace), token);

    ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> ContainsAsync(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.ContainsAsync(cacheKey, token)
            : ContainsAsync(new CacheKey(cacheKey), token);

    ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<TimeSpan?> TimeToLiveAsync(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.TimeToLiveAsync(cacheKey, token)
            : TimeToLiveAsync(new CacheKey(cacheKey), token);

    ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<DateTimeOffset?> ExpireTimeAsync(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.ExpireTimeAsync(cacheKey, token)
            : ExpireTimeAsync(new CacheKey(cacheKey), token);

    ValueTask<IDictionary<string, string?>?> GetMetadataAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, string?>?> GetMetadataAsync(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.GetMetadataAsync(cacheKey, token)
            : GetMetadataAsync(new CacheKey(cacheKey), token);

    ValueTask<bool> SetMetadataAsync(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);

    /// <inheritdoc cref="SetMetadataAsync(CacheKey, IDictionary{string, string}, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetMetadataAsync(Span<char> cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        SetMetadataAsync(new CacheKey(cacheKey), metadata, token);
}
