using System.Reactive.Subjects;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisPubSubTopicProvider : RedisTopicProviderBase
{
    private readonly RedisPubSubTopicOptions _options;
    private readonly IEventFormatterProxy<ICacheEvent> _formatter;
    private readonly IPolicyHolder _policyHolder;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Uri _sourceUri;
    private readonly IRedisChannelStrategy _redisChannelStrategy;
    private readonly CancellationTokenSource _stopTokenSource = new();
    private bool _disposed;

    public RedisPubSubTopicProvider(
        IOptions<RedisConnectionOptions> connectionOptionsAccessor,
        IOptions<RedisPubSubTopicOptions> optionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        IRedisConnector redis,
        IEventFormatterProxy<ICacheEvent> formatter,
        IPolicyHolder policyHolder,
        ILoggerFactory loggerFactory)
        : base(redis, optionsAccessor.Value.ConnectionMonitorEnabled ?? cacheOptionsAccessor.Value.ConnectionMonitorEnabled)
    {
        _options = optionsAccessor.Value;
        _formatter = formatter;
        _policyHolder = policyHolder;
        _loggerFactory = loggerFactory;
        var cacheOptions = cacheOptionsAccessor.Value;
        _sourceUri = cacheOptions.SourceUri ?? CacheOptions.MachineUri;
        _redisChannelStrategy = _options.RedisChannelStrategy ?? new PrefixStrategy(RedisTypePrefixes.PubSub, cacheOptions);
        Enabled = connectionOptionsAccessor.Value.Enabled && _options.Enabled;
    }

    public override string Name => KnownTopicNames.RedisPubSub;

    public override bool Enabled { get; }

    protected override ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey) =>
        new RedisPubSubTopic<ICacheEvent>(topicKey, _sourceUri, Redis, _redisChannelStrategy, () => new Subject<ICacheEvent>(), _formatter, _policyHolder, _options, _loggerFactory.Create<RedisPubSubTopic<ICacheEvent>>(), _stopTokenSource.Token);

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _stopTokenSource?.Cancel();
                _stopTokenSource?.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
