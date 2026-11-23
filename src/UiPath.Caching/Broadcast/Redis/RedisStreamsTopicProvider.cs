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
    private readonly BroadcastCapability _capability;
    private readonly StreamsProbeSettings _probeSettings;
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
        : this(redisStreamsTopicOptionsAccessor, cacheOptionsAccessor, registry, redis, formatter, resiliencePipelineProvider, loggerFactory, cachingTelemetryProvider, redisProfiler, pubSubFallback: null)
    {
    }

    /// <param name="pubSubFallback">The Pub/Sub provider the topics fall back to when the server does not run Streams, when <see cref="RedisStreamsTopicOptions.StreamsUnsupported"/> asks for it.</param>
    internal RedisStreamsTopicProvider(
        IOptions<RedisStreamsTopicOptions> redisStreamsTopicOptionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        PerTopicOptionsRegistry<RedisStreamsTopicOptions> registry,
        IRedisConnector redis,
        IEventFormatterProxy<ICacheEvent> formatter,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ILoggerFactory loggerFactory,
        ICachingTelemetryProvider cachingTelemetryProvider,
        IRedisProfiler redisProfiler,
        IPubSubFallback? pubSubFallback)
        : base(redis, cachingTelemetryProvider, redisProfiler, loggerFactory, redisStreamsTopicOptionsAccessor.Value.ConnectionMonitorEnabled ?? cacheOptionsAccessor.Value.ConnectionMonitorEnabled)
    {
        _redisStreamsTopicOptions = redisStreamsTopicOptionsAccessor.Value;
        _cacheOptions = cacheOptionsAccessor.Value;
        _registry = registry;
        _resiliencePipelineProvider = resiliencePipelineProvider;
        _formatter = formatter;
        _logger = loggerFactory.Create<RedisStreamsTopicProvider>();
        Enabled = _redisStreamsTopicOptions.Enabled;
        PubSubFallback = pubSubFallback;
        _capability = BroadcastCapability.For(redis);
        _probeSettings = StreamsProbeSettings.For(_redisStreamsTopicOptions, _cacheOptions, _logger);
        _capability.Attach(_probeSettings);
    }

    public override string Name => KnownTopicNames.RedisStreams;

    public override bool Enabled { get; }

    internal int StreamKeyCount => _streamKeys.Count;

    /// <summary>The Pub/Sub provider the topics fall back to, if one is wired.</summary>
    internal IPubSubFallback? PubSubFallback { get; }

    /// <summary>Starts the connector's Streams probe unless it runs, and waits for its decision. Not called in production, where the first topic starts the probe.</summary>
    /// <returns>The state at the end of <paramref name="timeout"/>: Unknown if the probe could not decide in time.</returns>
    internal async Task<StreamsState> DecideAsync(TimeSpan timeout, CancellationToken token = default)
    {
        _ = _capability.Start(_probeSettings);
        return await _capability.WaitDecidedAsync(timeout, token).ConfigureAwait(false);
    }

    /// <summary>The stream of each topic this provider has made and that is still on Streams, for the node node list.</summary>
    internal IReadOnlyList<RedisKey> StreamKeys()
    {
        List<RedisKey> streams = [];
        foreach (var topic in CreatedTopics())
        {
            if (topic is not StreamsFallbackTopic { OnStreams: false } && _streamKeys.TryGetValue(topic.TopicKey, out var stream))
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
        var action = _redisStreamsTopicOptions.StreamsUnsupported;
        if (action != StreamsUnsupportedAction.Disable && (action != StreamsUnsupportedAction.PubSub || PubSubFallback is null))
        {
            // Nothing to fall back to, so the topic is the Streams one, and a refusal only gets logged.
            var topic = CreateStreamsTopic(topicKey, resolved);
            // The topic's stream goes with it, so a provider that makes and drops many topics keeps no record of the dropped ones.
            topic.OnDisposed += (_, _) => _streamKeys.TryRemove(topicKey, out _);
            return topic;
        }

        return new StreamsFallbackTopic(
            topicKey,
            _capability,
            _probeSettings,
            action == StreamsUnsupportedAction.PubSub ? PubSubFallback : null,
            onRead => CreateStreamsTopic(topicKey, resolved, onRead),
            new KeyedSubject<ICacheEvent>(LoggerFactory.Create<KeyedSubject<ICacheEvent>>(), resolved.SlowObserverThreshold),
            _logger,
            onLeftStreams: () => _streamKeys.TryRemove(topicKey, out _),
            telemetry: Telemetry);
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

    private RedisStreamsTopic<ICacheEvent> CreateStreamsTopic(TopicKey topicKey, RedisStreamsTopicOptions resolved, Action? onRead = null) => new(
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
        onRefused: ex => _capability.Refused(ex),
        onRead: onRead,
        _stopTokenSource.Token);

    private RedisStreamsTopicOptions? ResolveOptions(TopicKey topicKey) =>
        _registry.Resolve(topicKey, _redisStreamsTopicOptions.Clone, _logger);
}
