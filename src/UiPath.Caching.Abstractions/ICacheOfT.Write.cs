#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface ICache<T>
{
    ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    ValueTask<bool> RemoveAsync(CacheKey[] cacheKeys, CancellationToken token = default);

    /// <inheritdoc cref="ICache.RemoveAsync{T}(CacheKey, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RemoveAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default) =>
        RemoveAsync(cacheKey.ToCacheKey(), token);

    /// <inheritdoc cref="ISpanKeyCache.RemoveManyAsync{T}(IEnumerable{CacheKey}, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> RemoveManyAsync(IEnumerable<CacheKey> cacheKeys, CancellationToken token = default) =>
        this is ISpanKeyCache<T> multi
            ? multi.RemoveManyAsync(cacheKeys, token)
            : MultiKeyFallback.RunAsync(cacheKeys, (Cache: this, Token: token), static k => k, static (s, k) => s.Cache.RemoveAsync(k, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, CancellationToken token = default) =>
        this is ISpanKeyCache<T> multi
            ? multi.SetManyAsync(keyValues, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync(kv.Key, kv.Value, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, TimeSpan expiration, CancellationToken token = default) =>
        this is ISpanKeyCache<T> multi
            ? multi.SetManyAsync(keyValues, expiration, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Expiration: expiration, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync(kv.Key, kv.Value, s.Expiration, s.Token));

    /// <inheritdoc cref="ISpanKeyCache.SetManyAsync{T}(IEnumerable{KeyValuePair{CacheKey, T}}, CachePolicy, CancellationToken)"/>
    sealed ValueTask<IReadOnlyList<CacheKey>> SetManyAsync(IEnumerable<KeyValuePair<CacheKey, T?>> keyValues, DateTimeOffset expiration, CancellationToken token = default) =>
        this is ISpanKeyCache<T> multi
            ? multi.SetManyAsync(keyValues, expiration, token)
            : MultiKeyFallback.RunAsync(keyValues, (Cache: this, Expiration: expiration, Token: token), static kv => kv.Key, static (s, kv) => s.Cache.SetAsync(kv.Key, kv.Value, s.Expiration, s.Token));

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync(ref CacheKeyHandler cacheKey, T? value, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), value, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, value, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync(ref CacheKeyHandler cacheKey, T? value, TimeSpan expiration, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), value, expiration, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, value, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync(ref CacheKeyHandler cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default)
    {
        if (this is not ISpanKeyCache<T> spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), value, expiration, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, value, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, CancellationToken token = default);

    ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CancellationToken token = default);

    /// <summary>
    /// Typed façade over
    /// <see cref="ICache.TryAddAsync{T}(CacheKey, T, TimeSpan, CachePolicy, CancellationToken)"/>;
    /// see that member for the contract. A successful add announces to peers.
    /// </summary>
    ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync(CacheKey, T, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> TryAddAsync(ref CacheKeyHandler cacheKey, T? value, CancellationToken token = default) =>
        TryAddAsync(cacheKey.ToCacheKey(), value, token);

    /// <inheritdoc cref="TryAddAsync(CacheKey, T, CancellationToken)"/>
    /// <param name="expiration">Lifetime of the entry if it is created.</param>
    ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync(CacheKey, T, TimeSpan, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> TryAddAsync(ref CacheKeyHandler cacheKey, T? value, TimeSpan expiration, CancellationToken token = default) =>
        TryAddAsync(cacheKey.ToCacheKey(), value, expiration, token);

    /// <inheritdoc cref="TryAddAsync(CacheKey, T, TimeSpan, CancellationToken)"/>
    ValueTask<bool> TryAddAsync(CacheKey cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="TryAddAsync(CacheKey, T, DateTimeOffset, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> TryAddAsync(ref CacheKeyHandler cacheKey, T? value, DateTimeOffset expiration, CancellationToken token = default) =>
        TryAddAsync(cacheKey.ToCacheKey(), value, expiration, token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default) =>
        RefreshAsync(cacheKey.ToCacheKey(), token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, TimeSpan, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync(ref CacheKeyHandler cacheKey, TimeSpan expiration, CancellationToken token = default) =>
        RefreshAsync(cacheKey.ToCacheKey(), expiration, token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, DateTimeOffset, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync(ref CacheKeyHandler cacheKey, DateTimeOffset expiration, CancellationToken token = default) =>
        RefreshAsync(cacheKey.ToCacheKey(), expiration, token);
}
