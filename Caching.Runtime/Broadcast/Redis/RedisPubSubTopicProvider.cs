using System.Collections.Concurrent;
using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public sealed class RedisPubSubTopicProvider : ITopicProvider
{
    private readonly ConcurrentDictionary<TopicKey, Lazy<ITopic<ICacheEvent>>> _topics = new();
    private readonly RedisPubSubTopicOptions _redisPubSubTopicOptions;
    private readonly Func<IDatabase> _databaseFactory;
    private readonly Func<ISubscriber> _subscriberFactory;
    private readonly IEventFormatterProxy<ICacheEvent> _formatter;
    private readonly IPolicyHolder _policyHolder;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Uri _sourceUri;
    private readonly IRedisChannelStrategy _redisChannelStrategy;

    public RedisPubSubTopicProvider(
        IOptions<RedisPubSubTopicOptions> redisPubSubTopicOptionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        Func<IDatabase> databaseFactory,
        Func<ISubscriber> subscriberFactory,
        IEventFormatterProxy<ICacheEvent> formatter,
        IPolicyHolder policyHolder,
        ILoggerFactory loggerFactory)
    {
        _redisPubSubTopicOptions = redisPubSubTopicOptionsAccessor.Value;
        _databaseFactory = databaseFactory;
        _subscriberFactory = subscriberFactory;
        _formatter = formatter;
        _policyHolder = policyHolder;
        _loggerFactory = loggerFactory;
        var cacheOptions = cacheOptionsAccessor.Value;
        _sourceUri = cacheOptions.SourceUri ?? CacheOptions.MachineUri;
        _redisChannelStrategy = _redisPubSubTopicOptions.RedisChannelStrategy ?? new PrefixStrategy(RedisTypePrefixes.PubSub, cacheOptions);
    }

    public string Name => KnownTopicNames.RedisPubSub;

    public bool Enabled => _redisPubSubTopicOptions.Enabled;

    public ITopic<ICacheEvent> CreateTopic(TopicKey topicKey) =>
        _topics.GetOrAdd(topicKey, tk => new Lazy<ITopic<ICacheEvent>>(() => CreateInternalTopic(tk))).Value;

    public void Dispose() => 
        _topics.Clear();

    private ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey) =>
        new RedisPubSubTopic<ICacheEvent>(
            topicKey,
            _sourceUri,
            _redisChannelStrategy,
            NewSubject,
            _databaseFactory,
            _subscriberFactory,
            _formatter,
            _policyHolder,
            _loggerFactory.Create<RedisPubSubTopic<ICacheEvent>>());

    private static ISubject<ICacheEvent> NewSubject() => new Subject<ICacheEvent>();
}
