#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

/// <remarks>
/// <para><b>Expiration.</b> The <c>expiration</c> parameter is not nullable. A caller with nothing to
/// say about lifetime calls the overload that has no <c>expiration</c> parameter and gets
/// <see cref="CachePolicy.DistributedExpiration"/>, then the cache default; a caller that passes one
/// means it, so a duration that is not positive — or a deadline that has already passed — is
/// rejected with <see cref="ArgumentOutOfRangeException"/> rather than silently ignored. See
/// <c>CacheExpiration</c>. The no-op implementations in this package read no argument at all
/// and so enforce nothing.</para>
/// </remarks>
public interface IHashCache : IDisposable
{
    string Name { get; }

    [OverloadResolutionPriority(1)]
    ValueTask<T?> GetItemAsync<T>(CacheKey cacheKey, string field, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<T?> GetItemAsync<T>(Span<char> cacheKey, string field, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetItemAsync<T>(cacheKey, field, policy, token)
            : GetItemAsync<T>(new CacheKey(cacheKey), field, policy, token);


    [OverloadResolutionPriority(1)]
    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);


    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetAsync<T>(Span<char> cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetAsync<T>(cacheKey, fields, policy, token)
            : GetAsync<T>(new CacheKey(cacheKey), fields, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetAsync<T>(cacheKey, policy, token)
            : GetAsync<T>(new CacheKey(cacheKey), policy, token);

    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetCacheEntryAsync<T>(cacheKey, policy, token)
            : GetCacheEntryAsync<T>(new CacheKey(cacheKey), policy, token);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, generator, expiration, setOption, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), generator, expiration, setOption, policy, token);

    /// <summary>Reads, and on a miss runs <paramref name="generator"/> with <paramref name="state"/>; a hit allocates no closure.</summary>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, policy, token);
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, policy, token);
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, policy, token);
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetOrAddAsync(cacheKey, state, generator, expiration, setOption, policy, token)
            : GetOrAddAsync(new CacheKey(cacheKey), state, generator, expiration, setOption, policy, token);
    }

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, values, policy, token)
            : SetAsync(new CacheKey(cacheKey), values, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, values, expiration, policy, token)
            : SetAsync(new CacheKey(cacheKey), values, expiration, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, values, expiration, policy, token)
            : SetAsync(new CacheKey(cacheKey), values, expiration, policy, token);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetAsync<T>(Span<char> cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.SetAsync(cacheKey, values, options, policy, token)
            : SetAsync(new CacheKey(cacheKey), values, options, policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RefreshAsync<T>(Span<char> cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(new CacheKey(cacheKey), policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, TimeSpan, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RefreshAsync<T>(Span<char> cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(new CacheKey(cacheKey), expiration, policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, DateTimeOffset, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RefreshAsync<T>(Span<char> cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(new CacheKey(cacheKey), expiration, policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, HashCacheEntryOptions, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RefreshAsync<T>(Span<char> cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(new CacheKey(cacheKey), options, policy, token);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ICache.RemoveAsync{T}(Span{char}, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RemoveAsync<T>(Span<char> cacheKey, CancellationToken token = default) =>
        RemoveAsync<T>(new CacheKey(cacheKey), token);

    /// <inheritdoc cref="ISpanKeyHashCache.RemoveFieldsAsync{T}(CacheKey, string[], CancellationToken)"/>
    /// <exception cref="NotSupportedException">The cache does not implement <see cref="ISpanKeyHashCache"/>.</exception>
    sealed ValueTask<bool> RemoveFieldsAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        this is ISpanKeyHashCache removal
            ? removal.RemoveFieldsAsync<T>(cacheKey, fields, token)
            : throw new NotSupportedException($"{GetType().Name} does not remove single fields of a hash.");

    /// <inheritdoc cref="RemoveFieldsAsync{T}(CacheKey, string[], CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RemoveFieldsAsync<T>(Span<char> cacheKey, string[] fields, CancellationToken token = default) =>
        RemoveFieldsAsync<T>(new CacheKey(cacheKey), fields, token);

    /// <inheritdoc cref="ISpanKeyCache.RemoveManyAsync{T}(IEnumerable{CacheKey}, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> RemoveManyAsync<T>(IEnumerable<CacheKey> cacheKeys, CancellationToken token = default) =>
        this is ISpanKeyHashCache multi
            ? multi.RemoveManyAsync<T>(cacheKeys, token)
            : MultiKeyFallback.RunAsync(cacheKeys, (Cache: this, Token: token), static k => k, static (s, k) => s.Cache.RemoveAsync<T>(k, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync<T>(IEnumerable<KeyValuePair<CacheKey, IDictionary<string, T?>>> keyValues, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyHashCache multi
            ? multi.SetManyAsync(keyValues, options, policy, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Options: options, Policy: policy, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync<T>(kv.Key, kv.Value, s.Options, s.Policy, s.Token));

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> ContainsAsync<T>(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.ContainsAsync<T>(cacheKey, token)
            : ContainsAsync<T>(new CacheKey(cacheKey), token);

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<TimeSpan?> TimeToLiveAsync<T>(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.TimeToLiveAsync<T>(cacheKey, token)
            : TimeToLiveAsync<T>(new CacheKey(cacheKey), token);

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.ExpireTimeAsync<T>(cacheKey, token)
            : ExpireTimeAsync<T>(new CacheKey(cacheKey), token);

    ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyHashCache spanKeyCache
            ? spanKeyCache.GetMetadataAsync<T>(cacheKey, token)
            : GetMetadataAsync<T>(new CacheKey(cacheKey), token);

    ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);

    /// <inheritdoc cref="SetMetadataAsync{T}(CacheKey, IDictionary{string, string}, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> SetMetadataAsync<T>(Span<char> cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        SetMetadataAsync<T>(new CacheKey(cacheKey), metadata, token);
}
