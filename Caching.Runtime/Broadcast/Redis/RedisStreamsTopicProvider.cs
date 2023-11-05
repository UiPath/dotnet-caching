using System.Collections.Concurrent;
using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public sealed class RedisStreamsTopicProvider : ITopicProvider
{
    private readonly RedisStreamsTopicOptions _redisStreamsTopicOptions;
    private readonly CacheOptions _cacheOptions;

    private readonly ConcurrentDictionary<TopicKey, Lazy<ITopic<ICacheEvent>>> _topics = new();

    private readonly IRedisConnector _redis;
    private readonly IEventFormatterProxy<ICacheEvent> _formatter;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IPolicyHolder _policyHolder;
    private readonly CancellationTokenSource _stopTokenSource = new();

    public RedisStreamsTopicProvider(
        IOptions<RedisStreamsTopicOptions> redisStreamsTopicOptionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        IRedisConnector redis,
        IEventFormatterProxy<ICacheEvent> formatter,
        IPolicyHolder policyHolder,
        ILoggerFactory loggerFactory)
    {
        _redisStreamsTopicOptions = redisStreamsTopicOptionsAccessor.Value;
        _cacheOptions = cacheOptionsAccessor.Value;
        _policyHolder = policyHolder;
        _redis = redis;
        _formatter = formatter;
        _loggerFactory = loggerFactory;
    }

    public string Name => KnownTopicNames.RedisStreams;

    public bool Enabled => _redisStreamsTopicOptions.Enabled;

    public ITopic<ICacheEvent> CreateTopic(TopicKey topicKey) =>
        _topics.GetOrAdd(topicKey, tk => new Lazy<ITopic<ICacheEvent>>(() => CreateInternalTopic(tk))).Value;

    public void Dispose()
    {
        _stopTokenSource.Cancel();
        _topics.Clear();
    }

    public ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey) =>
        new RedisStreamsTopic<ICacheEvent>(
            topicKey,
            _redis,
            NewSubject,
            _formatter,
            _policyHolder,
            _redisStreamsTopicOptions,
            _cacheOptions,
            _loggerFactory.Create<RedisStreamsTopic<ICacheEvent>>(),
            _stopTokenSource.Token);

    private static ISubject<ICacheEvent> NewSubject() => new Subject<ICacheEvent>();
}
