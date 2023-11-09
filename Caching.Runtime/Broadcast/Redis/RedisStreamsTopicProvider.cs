using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public  class RedisStreamsTopicProvider : TopicProviderBase
{
    private readonly RedisStreamsTopicOptions _redisStreamsTopicOptions;
    private readonly CacheOptions _cacheOptions;
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

    public override string Name => KnownTopicNames.RedisStreams;

    public override bool Enabled => _redisStreamsTopicOptions.Enabled;

    protected override ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey) =>
        new RedisStreamsTopic<ICacheEvent>(topicKey, _redis, () => new Subject<ICacheEvent>(), _formatter, _policyHolder, _redisStreamsTopicOptions, _cacheOptions, _loggerFactory.Create<RedisStreamsTopic<ICacheEvent>>(), _stopTokenSource.Token);
}
