using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisPubSubTopicProvider : TopicProviderBase
{
    private readonly RedisPubSubTopicOptions _options;
    private readonly IRedisConnector _redis;
    private readonly IEventFormatterProxy<ICacheEvent> _formatter;
    private readonly IPolicyHolder _policyHolder;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Uri _sourceUri;
    private readonly IRedisChannelStrategy _redisChannelStrategy;
    private readonly CancellationTokenSource _stopTokenSource = new();

    public RedisPubSubTopicProvider(
        IOptions<RedisPubSubTopicOptions> optionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        IRedisConnector redis,
        IEventFormatterProxy<ICacheEvent> formatter,
        IPolicyHolder policyHolder,
        ILoggerFactory loggerFactory)
    {
        _options = optionsAccessor.Value;
        _redis = redis;
        _formatter = formatter;
        _policyHolder = policyHolder;
        _loggerFactory = loggerFactory;
        var cacheOptions = cacheOptionsAccessor.Value;
        _sourceUri = cacheOptions.SourceUri ?? CacheOptions.MachineUri;
        _redisChannelStrategy = _options.RedisChannelStrategy ?? new PrefixStrategy(RedisTypePrefixes.PubSub, cacheOptions);
    }

    public override string Name => KnownTopicNames.RedisPubSub;

    public override bool Enabled => _options.Enabled;

    protected override ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey) =>
        new RedisPubSubTopic<ICacheEvent>(topicKey, _sourceUri, _redis, _redisChannelStrategy, () => new Subject<ICacheEvent>(), _formatter, _policyHolder, _options, _loggerFactory.Create<RedisPubSubTopic<ICacheEvent>>(), _stopTokenSource.Token);
}
