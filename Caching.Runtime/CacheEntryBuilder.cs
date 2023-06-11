namespace UiPath.Platform.Caching;

internal class CacheEntryBuilder
{
    private readonly string _cacheName;
    private readonly Type _cacheType;
    private readonly IKeyResolver _keyResolver;
    private readonly CacheClock _clock;

    public CacheEntryBuilder(
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

    public CacheEntryOptions BuildEntryOptions<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        if (cacheKey.IsNull)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }
        token.ThrowIfCancellationRequested();
        var entryCacheKey = _keyResolver.GetInternalCacheKey<T>(cacheKey, _cacheType, _cacheName);
        var topicKey = _keyResolver.GetTopicKey<T>(_cacheType, _cacheName);
        return new CacheEntryOptions
        {
            CacheKey = entryCacheKey,
            TopicKey = topicKey,
            Token = token,
            Expiration = _clock.ToDateTimeOffset(expiration)
        };
    }
}

