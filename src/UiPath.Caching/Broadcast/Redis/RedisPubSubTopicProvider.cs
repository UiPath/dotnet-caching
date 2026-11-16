using UiPath.Caching.Negotiation;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast.Redis;

public class RedisPubSubTopicProvider : RedisTopicProviderBase, IPubSubSubscriptions
{
    private readonly RedisPubSubTopicOptions _options;
    private readonly IEventFormatterProxy<ICacheEvent> _formatter;
    private readonly IResiliencePipelineProvider _resiliencePipelineProvider;
    private readonly PerTopicOptionsRegistry<RedisPubSubTopicOptions> _registry;
    private readonly Uri _sourceUri;
    private readonly IRedisChannelStrategy _defaultChannelStrategy;
    private readonly CancellationTokenSource _stopTokenSource = new();
    private readonly ILogger<RedisPubSubTopicProvider> _logger;
    private bool _disposed;

    public RedisPubSubTopicProvider(
        IOptions<RedisPubSubTopicOptions> optionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        PerTopicOptionsRegistry<RedisPubSubTopicOptions> registry,
        IRedisConnector redis,
        IEventFormatterProxy<ICacheEvent> formatter,
        IResiliencePipelineProvider resiliencePipelineProvider,
        ICachingTelemetryProvider telemetryProvider,
        IRedisProfiler redisProfiler,
        ILoggerFactory loggerFactory)
        : base(redis, telemetryProvider, redisProfiler, loggerFactory, optionsAccessor.Value.ConnectionMonitorEnabled ?? cacheOptionsAccessor.Value.ConnectionMonitorEnabled)
    {
        _options = optionsAccessor.Value;
        var cacheOptions = cacheOptionsAccessor.Value;
        _registry = registry;
        _formatter = formatter;
        _resiliencePipelineProvider = resiliencePipelineProvider;
        _logger = loggerFactory.Create<RedisPubSubTopicProvider>();
        _sourceUri = cacheOptions.SourceUri ?? CacheOptions.MachineUri;
        _defaultChannelStrategy = _options.RedisChannelStrategy ?? new PrefixStrategy(RedisKeyspaces.PubSub, cacheOptions);
        Enabled = _options.Enabled;
    }

    private event Action? SubscriptionConfirmed;

    public override string Name => KnownTopicNames.RedisPubSub;

    public override bool Enabled { get; }

    IReadOnlyList<string> IPubSubSubscriptions.Channels() =>
        [.. Topics().Where(topic => topic.Countable).Select(topic => topic.ChannelName).Distinct(StringComparer.Ordinal)];

    IReadOnlyList<string> IPubSubSubscriptions.Uncountable() =>
        [.. Topics().Where(topic => !topic.Countable).Select(topic => topic.ChannelName).Distinct(StringComparer.Ordinal)];

    // Each channel once: a connection holds one subscription to a channel, whatever the topics listening on it.
    IReadOnlyDictionary<string, int> IPubSubSubscriptions.Confirmed() =>
        Topics().Where(topic => topic.Countable && topic.IsSubscribed).Select(topic => topic.ChannelName).Distinct(StringComparer.Ordinal).ToDictionary(channel => channel, _ => 1, StringComparer.Ordinal);

    int IPubSubSubscriptions.Patterns() =>
        Topics().Where(topic => topic.IsPattern && topic.IsSubscribed).Select(topic => topic.ChannelName).Distinct(StringComparer.Ordinal).Count();

    void IPubSubSubscriptions.WhenConfirmed(Action handler) => SubscriptionConfirmed += handler;

    protected override ITopic<ICacheEvent> CreateInternalTopic(TopicKey topicKey)
    {
        var resolved = ResolveOptions(topicKey) ?? _options;
        var strategy = resolved.RedisChannelStrategy ?? _defaultChannelStrategy;
        var topic = new RedisPubSubTopic<ICacheEvent>(
            topicKey,
            _sourceUri,
            ConnectionState,
            Redis,
            strategy,
            () => new KeyedSubject<ICacheEvent>(LoggerFactory.Create<KeyedSubject<ICacheEvent>>(), resolved.SlowObserverThreshold),
            _formatter,
            _resiliencePipelineProvider,
            resolved,
            LoggerFactory.Create<RedisPubSubTopic<ICacheEvent>>(),
            _stopTokenSource.Token);
        topic.Subscribed += () => SubscriptionConfirmed?.Invoke();
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

    private IEnumerable<RedisPubSubTopic<ICacheEvent>> Topics() => CreatedTopics().OfType<RedisPubSubTopic<ICacheEvent>>();

    private RedisPubSubTopicOptions? ResolveOptions(TopicKey topicKey) =>
        _registry.Resolve(topicKey, _options.Clone, _logger);
}
