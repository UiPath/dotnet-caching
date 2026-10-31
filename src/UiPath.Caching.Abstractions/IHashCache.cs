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
    [OverloadResolutionPriority(2)]
    sealed ValueTask<T?> GetItemAsync<T>(ref CacheKeyHandler cacheKey, string field, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetItemAsync<T>(cacheKey.ToCacheKey(), field, policy, token);
        }

        try
        {
            return spanKeyCache.GetItemAsync<T>(cacheKey.Written, field, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }


    [OverloadResolutionPriority(1)]
    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);


    ValueTask<IDictionary<string, T?>> GetAsync<T>(CacheKey cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetAsync<T>(ref CacheKeyHandler cacheKey, string[] fields, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetAsync<T>(cacheKey.ToCacheKey(), fields, policy, token);
        }

        try
        {
            return spanKeyCache.GetAsync<T>(cacheKey.Written, fields, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetAsync<T>(ref CacheKeyHandler cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetAsync<T>(cacheKey.ToCacheKey(), policy, token);
        }

        try
        {
            return spanKeyCache.GetAsync<T>(cacheKey.Written, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<ICacheEntry<IDictionary<string, T?>>> GetCacheEntryAsync<T>(ref CacheKeyHandler cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetCacheEntryAsync<T>(cacheKey.ToCacheKey(), policy, token);
        }

        try
        {
            return spanKeyCache.GetCacheEntryAsync<T>(cacheKey.Written, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), generator, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, generator, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), generator, expiration, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, generator, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), generator, expiration, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, generator, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="ISpanKeyCache.GetOrAddAsync{T}(Span{char}, Func{CancellationToken, Task{T}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T>(ref CacheKeyHandler cacheKey, Func<CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), generator, expiration, setOption, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, generator, expiration, setOption, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <summary>Reads, and on a miss runs <paramref name="generator"/> with <paramref name="state"/>; a hit allocates no closure.</summary>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default);

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), state, generator, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, state, generator, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), state, generator, expiration, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, state, generator, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), state, generator, expiration, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, state, generator, expiration, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    /// <inheritdoc cref="IHashCache.GetOrAddAsync{T, TState}(CacheKey, TState, Func{TState, CancellationToken, Task{IDictionary{string, T}}}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, T?>> GetOrAddAsync<T, TState>(ref CacheKeyHandler cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset expiration, HashCacheSetOption? setOption, CachePolicy? policy, CancellationToken token = default)
    {
        if (generator is null)
        {
            cacheKey.Return();
            ArgumentNullException.ThrowIfNull(generator);
        }

        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetOrAddAsync(cacheKey.ToCacheKey(), state, generator, expiration, setOption, policy, token);
        }

        try
        {
            return spanKeyCache.GetOrAddAsync(cacheKey.Written, state, generator, expiration, setOption, policy, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

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

    ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> ContainsAsync<T>(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return ContainsAsync<T>(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.ContainsAsync<T>(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<TimeSpan?> TimeToLiveAsync<T>(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return TimeToLiveAsync<T>(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.TimeToLiveAsync<T>(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return ExpireTimeAsync<T>(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.ExpireTimeAsync<T>(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(CacheKey cacheKey, CancellationToken token = default);

    /// <inheritdoc cref="ISpanKeyCache.GetAsync{T}(Span{char}, CachePolicy, CancellationToken)"/>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<IDictionary<string, string?>?> GetMetadataAsync<T>(ref CacheKeyHandler cacheKey, CancellationToken token = default)
    {
        if (this is not ISpanKeyHashCache spanKeyCache)
        {
            return GetMetadataAsync<T>(cacheKey.ToCacheKey(), token);
        }

        try
        {
            return spanKeyCache.GetMetadataAsync<T>(cacheKey.Written, token);
        }
        finally
        {
            cacheKey.Return();
        }
    }

    ValueTask<bool> SetMetadataAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default);

    /// <inheritdoc cref="SetMetadataAsync{T}(CacheKey, IDictionary{string, string}, CancellationToken)"/>
    /// <remarks>Builds the key from the text, normalized as <c>new CacheKey(text)</c> normalizes it.</remarks>
    [OverloadResolutionPriority(2)]
    sealed ValueTask<bool> SetMetadataAsync<T>(ref CacheKeyHandler cacheKey, IDictionary<string, string?> metadata, CancellationToken token = default) =>
        SetMetadataAsync<T>(cacheKey.ToCacheKey(), metadata, token);
}
