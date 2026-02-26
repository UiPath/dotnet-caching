using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisStreamsTopicProvider : RedisTopicProviderBase
{
    private readonly RedisStreamsTopicOptions _redisStreamsTopicOptions;
    private readonly CacheOptions _cacheOptions;
    private readonly IEventFormatterProxy<ICacheEvent> _formatter;
    private readonly IResiliencePipelineHolder _resiliencePipelineHolder;
    private readonly CancellationTokenSource _stopTokenSource = new();
    private bool _disposed;

    public RedisStreamsTopicProvider(
        IOptions<RedisStreamsTopicOptions> redisStreamsTopicOptionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        IRedisConnector redis,
        IEventFormatterProxy<ICacheEvent> formatter,
        IResiliencePipelineHolder resiliencePipelineHolder,
        ILoggerFactory loggerFactory,
        ICachingTelemetryProvider cachingTelemetryProvider,
        IRedisProfiler redisProfiler)
        : base(redis, cachingTelemetryProvider, redisProfiler, loggerFactory, redisStreamsTopicOptionsAccessor.Value.ConnectionMonitorEnabled ?? cacheOptionsAccessor.Value.ConnectionMonitorEnabled)
    {
        _redisStreamsTopicOptions = redisStreamsTopicOptionsAccessor.Value;
        _cacheOptions = cacheOptionsAccessor.Value;
        _resiliencePipelineHolder = resiliencePipelineHolder;
        _formatter = formatter;
        Enabled = _redisStreamsTopicOptions.Enabled;
    }

    public override string Name => KnownTopicNames.RedisStreams;

    public override bool Enabled { get; }

    protected override ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey) =>
        new RedisStreamsTopic<ICacheEvent>(
            topicKey,
            ConnectionState,
            Redis,
            () => new KeyedSubject<ICacheEvent>(),
            _formatter,
            _resiliencePipelineHolder,
            _redisStreamsTopicOptions,
            _cacheOptions,
            LoggerFactory.Create<RedisStreamsTopic<ICacheEvent>>(),
            Telemetry,
            Profiler,
            _stopTokenSource.Token);

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
