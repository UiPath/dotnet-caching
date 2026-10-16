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

    /// <inheritdoc cref="IStatefulHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IStatefulHashCache<T> stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, token)
            : GetOrAddAsync(cacheKey, StatefulCacheExtensions.BindHash(state, generator), token);
    }

    /// <inheritdoc cref="IStatefulHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IStatefulHashCache<T> stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : GetOrAddAsync(cacheKey, StatefulCacheExtensions.BindHash(state, generator), expiration, token);
    }

    /// <inheritdoc cref="IStatefulHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IStatefulHashCache<T> stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : GetOrAddAsync(cacheKey, StatefulCacheExtensions.BindHash(state, generator), expiration, token);
    }

    /// <inheritdoc cref="IStatefulHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IStatefulSpanKeyHashCache<T> stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, token)
            : GetOrAddAsync(cacheKey, StatefulCacheExtensions.BindHash(state, generator), token);
    }

    /// <inheritdoc cref="IStatefulHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IStatefulSpanKeyHashCache<T> stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : GetOrAddAsync(cacheKey, StatefulCacheExtensions.BindHash(state, generator), expiration, token);
    }

    /// <inheritdoc cref="IStatefulHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return this is IStatefulSpanKeyHashCache<T> stateful
            ? stateful.GetOrAddAsync(cacheKey, state, generator, expiration, token)
            : GetOrAddAsync(cacheKey, StatefulCacheExtensions.BindHash(state, generator), expiration, token);
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

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default);

    ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ICache.RemoveAsync{T}(Span{char}, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> RemoveAsync(Span<char> cacheKey, CancellationToken token = default) =>
        RemoveAsync(new CacheKey(cacheKey), token);

    ValueTask<bool> ContainsAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(-1)]
    sealed ValueTask<bool> ContainsAsync(Span<char> cacheKey, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> spanKeyCache
            ? spanKeyCache.ContainsAsync(cacheKey, token)
            : ContainsAsync(new CacheKey(cacheKey), token);

    ValueTask<TimeSpan?> TimeToLiveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<DateTimeOffset?> ExpireTimeAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<IDictionary<string, string?>?> GetMetadataAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> SetMetadataAsync(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);
}
