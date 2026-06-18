namespace UiPath.Caching;

internal class HashCacheEntryBuilder
{
    private readonly ICacheKeyStrategy _cacheKeyStrategy;
    private readonly ITopicKeyStrategy _topicKeyStrategy;
    private readonly CacheClock _clock;

    public HashCacheEntryBuilder(
        ICacheKeyStrategy cacheKeyStrategy,
        ITopicKeyStrategy topicKeyStrategy,
        CacheClock clock)
    {
        _cacheKeyStrategy = cacheKeyStrategy;
        _topicKeyStrategy = topicKeyStrategy;
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
        var entryCacheKey = _cacheKeyStrategy.GetCacheKey<T>(cacheKey);
        var topicKey = _topicKeyStrategy.GetTopicKey<T>();
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
