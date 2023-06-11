namespace UiPath.Platform.Caching;

internal class HashCacheEntryBuilder
{
    private readonly string _cacheName;
    private readonly Type _cacheType;
    private readonly IKeyResolver _keyResolver;
    private readonly CacheClock _clock;

    public HashCacheEntryBuilder(
        string cacheName,
        Type cacheType,
        IKeyResolver keyResolver,
        CacheClock clock)
    {
        _cacheName = cacheName;
        _cacheType = cacheType;
        _keyResolver = keyResolver;
        _clock = clock;
    }


    internal InternalHashCacheEntryOptions BuildEntryOptions<T>(CacheKey cacheKey, CancellationToken token = default)
        => BuildEntryOptions<T>(cacheKey, default, token: token);

    internal InternalHashCacheEntryOptions BuildEntryOptions<T>(CacheKey cacheKey, DateTimeOffset? expiration, HashCacheSetOption setOption = HashCacheSetOption.KeyReplace, CancellationToken token = default)
        => BuildEntryOptions<T>(cacheKey, default, expiration, setOption, token);

    internal InternalHashCacheEntryOptions BuildEntryOptions<T>(CacheKey cacheKey, string[]? fields, DateTimeOffset? expiration, HashCacheSetOption setOption = HashCacheSetOption.KeyReplace, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (cacheKey.IsNull)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }
        var entryCacheKey = _keyResolver.GetInternalCacheKey<T>(cacheKey, _cacheType, _cacheName);
        var topicKey = _keyResolver.GetTopicKey<T>(_cacheType, _cacheName);
        return new InternalHashCacheEntryOptions { 
            CacheKey = entryCacheKey,
            Fields = fields,
            TopicKey = topicKey,
            Token = token,
            Expiration = _clock.ToDateTimeOffset(expiration),
            SetOption = setOption,
            Metadata = default
        };
    }
}
