#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface IHashCache<T>
{
    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CancellationToken token = default);

    ValueTask<bool> SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync(ref CacheKeyHandler cacheKey, IDictionary<string, T?> values, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), values, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, values, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync(ref CacheKeyHandler cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), values, expiration, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, values, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync(ref CacheKeyHandler cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), values, expiration, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, values, expiration, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync(ref CacheKeyHandler cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache<T> spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), values, options, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, values, options, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default) =>
        RefreshAsync(cacheKey.ToCacheKey(), token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, TimeSpan, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync(ref CacheKeyHandler cacheKey, TimeSpan expiration, CancellationToken token = default) =>
        RefreshAsync(cacheKey.ToCacheKey(), expiration, token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, DateTimeOffset, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync(ref CacheKeyHandler cacheKey, DateTimeOffset expiration, CancellationToken token = default) =>
        RefreshAsync(cacheKey.ToCacheKey(), expiration, token);

    ValueTask<bool> RefreshAsync(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync(CacheKey, HashCacheEntryOptions, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync(ref CacheKeyHandler cacheKey, HashCacheEntryOptions options, CancellationToken token = default) =>
        RefreshAsync(cacheKey.ToCacheKey(), options, token);

    ValueTask<bool> RemoveAsync(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ICache.RemoveAsync{T}(CacheKey, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RemoveAsync(ref CacheKeyHandler cacheKey, CancellationToken token = default) =>
        RemoveAsync(cacheKey.ToCacheKey(), token);

    /// <inheritdoc cref="ISpanKeyHashCache.RemoveFieldsAsync{T}(CacheKey, string[], CancellationToken)"/>
    /// <exception cref="NotSupportedException">The cache does not implement <see cref="ISpanKeyHashCache{T}"/>.</exception>
    sealed ValueTask<bool> RemoveFieldsAsync(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        this is ISpanKeyHashCache<T> removal
            ? removal.RemoveFieldsAsync(cacheKey, fields, token)
            : throw new NotSupportedException($"{GetType().Name} does not remove single fields of a hash.");

    /// <inheritdoc cref="RemoveFieldsAsync(CacheKey, string[], CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RemoveFieldsAsync(ref CacheKeyHandler cacheKey, string[] fields, CancellationToken token = default) =>
        RemoveFieldsAsync(cacheKey.ToCacheKey(), fields, token);

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

    ValueTask<bool> SetMetadataAsync(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);

    /// <inheritdoc cref="SetMetadataAsync(CacheKey, IDictionary{string, string}, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetMetadataAsync(ref CacheKeyHandler cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        SetMetadataAsync(cacheKey.ToCacheKey(), metadata, token);
}
