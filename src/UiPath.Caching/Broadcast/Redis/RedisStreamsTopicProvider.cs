using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast.Redis;

public class RedisStreamsTopicProvider : RedisTopicProviderBase
{
    private readonly RedisStreamsTopicOptions _redisStreamsTopicOptions;
    private readonly CacheOptions _cacheOptions;
    private readonly IEventFormatterProxy<ICacheEvent> _formatter;
    private readonly IResiliencePipelineProvider _resiliencePipelineProvider;
    private readonly PerTopicOptionsRegistry<RedisStreamsTopicOptions> _registry;
    private readonly CancellationTokenSource _stopTokenSource = new();
    private readonly ILogger<RedisStreamsTopicProvider> _logger;
    private bool _disposed;

    public RedisStreamsTopicProvider(
        IOptions<RedisStreamsTopicOptions> redisStreamsTopicOptionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        PerTopicOptionsRegistry<RedisStreamsTopicOptions> registry,
        IRedisConnector redis,
        IEventFormatterProxy<ICacheEvent> formatter,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ILoggerFactory loggerFactory,
        ICachingTelemetryProvider cachingTelemetryProvider,
        IRedisProfiler redisProfiler)
        : base(redis, cachingTelemetryProvider, redisProfiler, loggerFactory, redisStreamsTopicOptionsAccessor.Value.ConnectionMonitorEnabled ?? cacheOptionsAccessor.Value.ConnectionMonitorEnabled)
    {
        _redisStreamsTopicOptions = redisStreamsTopicOptionsAccessor.Value;
        _cacheOptions = cacheOptionsAccessor.Value;
        _registry = registry;
        _resiliencePipelineProvider = resiliencePipelineProvider;
        _formatter = formatter;
        _logger = loggerFactory.Create<RedisStreamsTopicProvider>();
        Enabled = _redisStreamsTopicOptions.Enabled;
    }

    public override string Name => KnownTopicNames.RedisStreams;

    public override bool Enabled { get; }

    protected override ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey)
    {
        var resolved = ResolveOptions(topicKey) ?? _redisStreamsTopicOptions;
        return new RedisStreamsTopic<ICacheEvent>(
            topicKey,
            ConnectionState,
            Redis,
            () => new KeyedSubject<ICacheEvent>(LoggerFactory.Create<KeyedSubject<ICacheEvent>>(), resolved.SlowObserverThreshold),
            _formatter,
            _resiliencePipelineProvider,
            resolved,
            _cacheOptions,
            LoggerFactory.Create<RedisStreamsTopic<ICacheEvent>>(),
            Telemetry,
            Profiler,
            _stopTokenSource.Token);
    }

    private RedisStreamsTopicOptions? ResolveOptions(TopicKey topicKey) =>
        _registry.Resolve(topicKey, _redisStreamsTopicOptions.Clone, _logger);

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
