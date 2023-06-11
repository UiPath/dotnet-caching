using System.Collections.Concurrent;
using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public sealed class RedisPubSubTopicProvider : ITopicProvider
{
    private readonly ConcurrentDictionary<TopicKey, Lazy<ITopic<ICacheEvent>>> _topics = new();
    private readonly RedisPubSubTopicOptions _redisPubSubTopicOptions;
    private readonly RedisCacheOptions _redisOptions;
    private readonly CacheOptions _cacheOptions;
    private readonly IRedisConnection _redisConnection;
    private readonly IEventFormatterProxy<ICacheEvent> _formatter;
    private readonly IKeyResolver _keyResolver;
    private readonly IPolicyHolder _policyHolder;
    private readonly ILoggerFactory _loggerFactory;

    public RedisPubSubTopicProvider(
        IOptions<RedisPubSubTopicOptions> redisPubSubTopicOptionsAccessor,
        IOptions<RedisCacheOptions> redisCacheOptionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        IRedisConnection redisConnection,
        IEventFormatterProxy<ICacheEvent> formatter,
        IKeyResolver keyResolver,
        IPolicyHolder policyHolder,
        ILoggerFactory loggerFactory)
    {
        _redisPubSubTopicOptions = redisPubSubTopicOptionsAccessor.Value;
        _redisOptions = redisCacheOptionsAccessor.Value;
        _cacheOptions = cacheOptionsAccessor.Value;
        _redisConnection = redisConnection;
        _formatter = formatter;
        _keyResolver = keyResolver;
        _policyHolder = policyHolder;
        _loggerFactory = loggerFactory;
    }

    public string Name => KnownTopicNames.RedisPubSub;

    public bool Enabled => _redisPubSubTopicOptions.Enabled;

    public ITopic<ICacheEvent> CreateTopic(TopicKey topicKey)
    {
        return _topics.GetOrAdd(topicKey, tk => new Lazy<ITopic<ICacheEvent>>(() => CreateInternalTopic(tk))).Value;
    }

    public void Dispose()
    {
        _topics.Clear();
    }

    public ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey)
    {
        var cnn = _redisConnection.Connection;
        return new RedisPubSubTopic<ICacheEvent>(
            topicKey,
            () => new Subject<ICacheEvent>(),
            cnn.GetDatabase(),
            cnn.GetSubscriber(),
            _formatter,
            _keyResolver,
            _policyHolder,
            _redisOptions,
            _cacheOptions,
            _loggerFactory.Create<RedisPubSubTopic<ICacheEvent>>());
    }
}
