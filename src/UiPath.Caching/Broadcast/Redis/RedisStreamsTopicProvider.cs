using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<TopicKey, RedisKey> _streamKeys = new();
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

    internal int StreamKeyCount => _streamKeys.Count;

    /// <summary>The stream of each topic this provider has made, for the node node list.</summary>
    internal IReadOnlyList<RedisKey> StreamKeys()
    {
        List<RedisKey> streams = [];
        foreach (var key in Keys)
        {
            if (_streamKeys.TryGetValue(key, out var stream))
            {
                streams.Add(stream);
            }
        }

        return streams;
    }

    protected override ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey)
    {
        var resolved = ResolveOptions(topicKey) ?? _redisStreamsTopicOptions;
        _streamKeys[topicKey] = (resolved.RedisStreamKeyStrategy ?? new PrefixStrategy(RedisKeyspaces.Streams, _cacheOptions)).GetRedisKey(topicKey);
        var topic = new RedisStreamsTopic<ICacheEvent>(
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

        // The topic's stream goes with it, so a provider that makes and drops many topics keeps no record of the dropped ones.
        topic.OnDisposed += (_, _) => _streamKeys.TryRemove(topicKey, out _);
        return topic;
    }

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

    private RedisStreamsTopicOptions? ResolveOptions(TopicKey topicKey) =>
        _registry.Resolve(topicKey, _redisStreamsTopicOptions.Clone, _logger);
}
