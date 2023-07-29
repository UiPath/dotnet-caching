namespace UiPath.Platform.Caching;

internal class CacheEntryBuilder
{
    private readonly ICacheKeyStrategy _cacheKeyStrategy;
    private readonly ITopicKeyStrategy _topicKeyStrategy;
    private readonly CacheClock _clock;

    public CacheEntryBuilder(
        ICacheKeyStrategy cacheKeyStrategy,
        ITopicKeyStrategy topicKeyStrategy,
        CacheClock clock)
    {
        _cacheKeyStrategy = cacheKeyStrategy;
        _topicKeyStrategy = topicKeyStrategy;
        _clock = clock;
    }

    public CacheEntryOptions BuildEntryOptions<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        if (cacheKey.IsNull)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }
        token.ThrowIfCancellationRequested();
        var entryCacheKey = _cacheKeyStrategy.GetCacheKey<T>(cacheKey);
        var topicKey = _topicKeyStrategy.GetTopicKey<T>();
        return new CacheEntryOptions
        {
            CacheKey = entryCacheKey,
            TopicKey = topicKey,
            Token = token,
            Expiration = _clock.ToDateTimeOffset(expiration)
        };
    }
}

