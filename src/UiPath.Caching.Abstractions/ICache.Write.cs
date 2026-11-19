#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface ICache
{
    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default);

    /// <summary>Removes by the key written as an interpolated string, normalized as <c>new CacheKey(text)</c> normalizes it.</summary>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RemoveAsync<T>(ref CacheKeyHandler cacheKey, CancellationToken token = default) =>
        RemoveAsync<T>(cacheKey.ToCacheKey(), token);

    /// <inheritdoc cref="ISpanKeyCache.RemoveManyAsync{T}(IEnumerable{CacheKey}, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> RemoveManyAsync<T>(IEnumerable<CacheKey> cacheKeys, CancellationToken token = default) =>
        this is ISpanKeyCache multi
            ? multi.RemoveManyAsync<T>(cacheKeys, token)
            : MultiKeyFallback.RunAsync(cacheKeys, (Cache: this, Token: token), static k => k, static (s, k) => s.Cache.RemoveAsync<T>(k, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync<T>(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache multi
            ? multi.SetManyAsync(keyValues, policy, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Policy: policy, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync<T>(kv.Key, kv.Value, s.Policy, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync<T>(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache multi
            ? multi.SetManyAsync(keyValues, expiration, policy, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Expiration: expiration, Policy: policy, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync<T>(kv.Key, kv.Value, s.Expiration, s.Policy, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync<T>(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        this is ISpanKeyCache multi
            ? multi.SetManyAsync(keyValues, expiration, policy, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Expiration: expiration, Policy: policy, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync<T>(kv.Key, kv.Value, s.Expiration, s.Policy, s.Token));

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync<T>(ref CacheKeyHandler cacheKey, T? value, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), value, policy, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, value, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync<T>(ref CacheKeyHandler cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), value, expiration, policy, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, value, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync<T>(ref CacheKeyHandler cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), value, expiration, policy, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, value, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <summary>
    /// Writes <paramref name="value"/> only if <paramref name="cacheKey"/> is absent. Redis
    /// <c>SET … NX</c>, one atomic round-trip. A successful add announces to peers.
    /// </summary>
    /// <returns>
    /// <c>true</c> only if this call created the key. <c>false</c> conflates "it existed" with "the
    /// write could not be completed", deliberately and fail-closed. Never deletes; not a lock.
    /// </returns>
    ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> TryAddAsync<T>(ref CacheKeyHandler cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) =>
        TryAddAsync(cacheKey.ToCacheKey(), value, policy, token);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, CachePolicy, CancellationToken)"/>
    /// <param name="expiration">
    /// Lifetime of the entry if it is created, applied by the same atomic command. Must be a
    /// positive duration; to inherit <c>CachePolicy.DistributedExpiration</c> and then the cache
    /// default, call the overload without this parameter.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expiration"/> is not positive.</exception>
    ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, TimeSpan, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> TryAddAsync<T>(ref CacheKeyHandler cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddAsync(cacheKey.ToCacheKey(), value, expiration, policy, token);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, TimeSpan, CachePolicy, CancellationToken)"/>
    ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync{T}(CacheKey, T, DateTimeOffset, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> TryAddAsync<T>(ref CacheKeyHandler cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddAsync(cacheKey.ToCacheKey(), value, expiration, policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync<T>(ref CacheKeyHandler cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey.ToCacheKey(), policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, TimeSpan, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync<T>(ref CacheKeyHandler cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey.ToCacheKey(), expiration, policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, DateTimeOffset, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync<T>(ref CacheKeyHandler cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey.ToCacheKey(), expiration, policy, token);
}
