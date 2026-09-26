namespace UiPath.Caching;

internal sealed class HashCacheEntryBuilder
{
    private readonly ICacheKeyStrategy _cacheKeyStrategy;
    private readonly ITopicKeyStrategy _topicKeyStrategy;
    private readonly TimeProvider _clock;

    public HashCacheEntryBuilder(
        ICacheKeyStrategy cacheKeyStrategy,
        ITopicKeyStrategy topicKeyStrategy,
        TimeProvider clock)
    {
        _cacheKeyStrategy = cacheKeyStrategy;
        _topicKeyStrategy = topicKeyStrategy;
        _clock = clock;
    }

    internal ICacheKeyStrategy KeyStrategy => _cacheKeyStrategy;

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
        if (entryCacheKey.IsNull)
        {
            throw new InvalidOperationException($"The cache key strategy {_cacheKeyStrategy.GetType().FullName} returned an empty key.");
        }

        var topicKey = _topicKeyStrategy.GetTopicKey<T>();
        return new InternalHashCacheEntryOptions {
            CacheKey = entryCacheKey,
            CallerKey = cacheKey,
            Fields = fields,
            TopicKey = topicKey,
            Token = token,
            Expiration = _clock.ToDateTimeOffset(expiration),
            SetOption = setOption,
            Metadata = default,
        };
    }
}
