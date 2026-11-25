namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    private async ValueTask<IDictionary<string, T?>> GetOrAddCoreAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, DateTimeOffset effectiveExpiration, HashCacheSetOption setOption, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var (found, cached, unreadable) = await GetInnerWithFoundAsync<T?>(cacheKey, policy, token).ConfigureAwait(false);
        if (found)
        {
            return cached;
        }

        // A field this call cannot read would survive a write that only sets fields, and every read after would miss again until the key expires.
        if (unreadable && setOption == HashCacheSetOption.HashReplace)
        {
            setOption = HashCacheSetOption.KeyReplace;
        }

        LogCacheMissed(Logged(cacheKey, typeof(T)));
        var ret = await RunGeneratorAsync(cacheKey, state, generator, (policy ?? DefaultPolicy)?.FactoryTimeout, token).ConfigureAwait(false);
        if (ret.Count > 0)
        {
            var options = new HashCacheEntryOptions(effectiveExpiration, default, default, setOption);
            await SetAsync(cacheKey, ret, options, policy, token).ConfigureAwait(false);
        }
        else if (_cacheNullValues)
        {
            var options = new HashCacheEntryOptions(effectiveExpiration, default, default, setOption);
            await SetEmptyMarkerAsync<T>(cacheKey, options, policy, token).ConfigureAwait(false);
        }
        else
        {
            await RemoveAsync<T>(cacheKey, token).ConfigureAwait(false);
        }

        return ret;
    }

    private Task<IDictionary<string, T?>> RunGeneratorAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<IDictionary<string, T?>>> generator, TimeSpan? factoryTimeout, CancellationToken token) =>
        factoryTimeout is null || factoryTimeout.Value <= TimeSpan.Zero
            ? generator(state, token)
            : FactoryTimeout.RunAsync(ct => generator(state, ct), factoryTimeout, cacheKey, Name, Telemetry, token, clock: Clock);

    private ValueTask<bool> SetEmptyMarkerAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, CancellationToken token)
    {
        var redisKey = ToRedisKey(cacheKey, token);
        var writer = FormatFor(policy, forWrite: true).Writer;
        var expiration = GetExpiration(options, policy);
        if (options.Metadata is not { Count: > 0 } metadataValues)
        {
            return SetInnerAsync<T>(cacheKey, redisKey, [new HashEntry(KnownFieldNames.MetadataKey, RedisValue.EmptyString)], [], HashCacheSetOption.KeyReplace, expiration, token);
        }

        var (metadata, payload) = SerializeFieldValue(metadataValues, writer);
        return SetInnerAsync<T>(cacheKey, redisKey, [new HashEntry(KnownFieldNames.MetadataKey, metadata)], [payload], HashCacheSetOption.KeyReplace, expiration, token);
    }
}
