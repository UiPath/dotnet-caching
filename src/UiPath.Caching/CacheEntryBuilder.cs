namespace UiPath.Caching;

internal sealed class CacheEntryBuilder
{
    private readonly ICacheKeyStrategy _cacheKeyStrategy;
    private readonly ITopicKeyStrategy _topicKeyStrategy;
    private readonly TimeProvider _clock;

    public CacheEntryBuilder(
        ICacheKeyStrategy cacheKeyStrategy,
        ITopicKeyStrategy topicKeyStrategy,
        TimeProvider clock)
    {
        _cacheKeyStrategy = cacheKeyStrategy;
        _topicKeyStrategy = topicKeyStrategy;
        _clock = clock;
    }

    public ICacheKeyStrategy KeyStrategy => _cacheKeyStrategy;

    public CacheEntryOptions BuildEntryOptions<T>(CacheKey cacheKey, DateTimeOffset? expiration = null, CancellationToken token = default)
    {
        if (cacheKey.IsNull)
        {
            throw new ArgumentNullException(nameof(cacheKey));
        }
        token.ThrowIfCancellationRequested();
        var entryCacheKey = _cacheKeyStrategy.GetCacheKey<T>(cacheKey);
        if (entryCacheKey.IsNull)
        {
            throw new InvalidOperationException($"The cache key strategy {_cacheKeyStrategy.GetType().FullName} returned an empty key.");
        }

        var topicKey = _topicKeyStrategy.GetTopicKey<T>();
        return new CacheEntryOptions
        {
            CacheKey = entryCacheKey,
            CallerKey = cacheKey,
            TopicKey = topicKey,
            Token = token,
            Expiration = _clock.ToDateTimeOffset(expiration),
        };
    }
}
