#pragma warning disable S3060 // by design: a cache with the capability interface takes its path, any other (a mock) falls back to the CacheKey members
namespace UiPath.Caching;

public partial interface IHashCache
{
    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<bool> SetAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync<T>(ref CacheKeyHandler cacheKey, IDictionary<string, T?> values, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), values, policy, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, values, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync<T>(ref CacheKeyHandler cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), values, expiration, policy, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, values, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync<T>(ref CacheKeyHandler cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), values, expiration, policy, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, values, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.SetAsync{T}(Span{char}, T, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetAsync<T>(ref CacheKeyHandler cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return SetAsync(cacheKey.ToCacheKey(), values, options, policy, token);
        }

        try
        {
            return spanKeyCache.SetAsync(cacheKey.Written, values, options, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync<T>(ref CacheKeyHandler cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey.ToCacheKey(), policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, TimeSpan, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync<T>(ref CacheKeyHandler cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey.ToCacheKey(), expiration, policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, DateTimeOffset, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync<T>(ref CacheKeyHandler cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey.ToCacheKey(), expiration, policy, token);

    ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="RefreshAsync{T}(CacheKey, HashCacheEntryOptions, CachePolicy, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RefreshAsync<T>(ref CacheKeyHandler cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token = default) =>
        RefreshAsync<T>(cacheKey.ToCacheKey(), options, policy, token);

    ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ICache.RemoveAsync{T}(CacheKey, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RemoveAsync<T>(ref CacheKeyHandler cacheKey, CancellationToken token = default) =>
        RemoveAsync<T>(cacheKey.ToCacheKey(), token);

    /// <inheritdoc cref="ISpanKeyHashCache.RemoveFieldsAsync{T}(CacheKey, string[], CancellationToken)"/>
    /// <exception cref="NotSupportedException">The cache does not implement <see cref="ISpanKeyHashCache"/>.</exception>
    sealed ValueTask<bool> RemoveFieldsAsync<T>(CacheKey cacheKey, string[] fields, CancellationToken token = default) =>
        this is ISpanKeyHashCache removal
            ? removal.RemoveFieldsAsync<T>(cacheKey, fields, token)
            : throw new NotSupportedException($"{GetType().Name} does not remove single fields of a hash.");

    /// <inheritdoc cref="RemoveFieldsAsync{T}(CacheKey, string[], CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> RemoveFieldsAsync<T>(ref CacheKeyHandler cacheKey, string[] fields, CancellationToken token = default) =>
        RemoveFieldsAsync<T>(cacheKey.ToCacheKey(), fields, token);

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

    ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);

    /// <inheritdoc cref="SetMetadataAsync{T}(CacheKey, IDictionary{string, string}, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetMetadataAsync<T>(ref CacheKeyHandler cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        SetMetadataAsync<T>(cacheKey.ToCacheKey(), metadata, token);
}
