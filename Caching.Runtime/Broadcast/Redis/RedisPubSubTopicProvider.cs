using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisPubSubTopicProvider : RedisTopicProviderBase
{
    private readonly RedisPubSubTopicOptions _options;
    private readonly IEventFormatterProxy<ICacheEvent> _formatter;
    private readonly IResiliencePipelineHolder _resiliencePipelineHolder;
    private readonly Uri _sourceUri;
    private readonly IRedisChannelStrategy _redisChannelStrategy;
    private readonly CancellationTokenSource _stopTokenSource = new();
    private bool _disposed;

    public RedisPubSubTopicProvider(
        IOptions<RedisPubSubTopicOptions> optionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        IRedisConnector redis,
        IEventFormatterProxy<ICacheEvent> formatter,
        IResiliencePipelineHolder resiliencePipelineHolder,
        ICachingTelemetryProvider telemetryProvider,
        IRedisProfiler redisProfiler,
        ILoggerFactory loggerFactory)
        : base(redis, telemetryProvider, redisProfiler, loggerFactory, optionsAccessor.Value.ConnectionMonitorEnabled ?? cacheOptionsAccessor.Value.ConnectionMonitorEnabled)
    {
        _options = optionsAccessor.Value;
        _formatter = formatter;
        _resiliencePipelineHolder = resiliencePipelineHolder;
        var cacheOptions = cacheOptionsAccessor.Value;
        _sourceUri = cacheOptions.SourceUri ?? CacheOptions.MachineUri;
        _redisChannelStrategy = _options.RedisChannelStrategy ?? new PrefixStrategy(RedisTypePrefixes.PubSub, cacheOptions);
        Enabled = _options.Enabled;
    }

    public override string Name => KnownTopicNames.RedisPubSub;

    public override bool Enabled { get; }

    protected override ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey) =>
        new RedisPubSubTopic<ICacheEvent>(topicKey, _sourceUri, ConnectionState, Redis, _redisChannelStrategy, () => new KeyedSubject<ICacheEvent>(), _formatter, _resiliencePipelineHolder, _options, LoggerFactory.Create<RedisPubSubTopic<ICacheEvent>>(), _stopTokenSource.Token);

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
