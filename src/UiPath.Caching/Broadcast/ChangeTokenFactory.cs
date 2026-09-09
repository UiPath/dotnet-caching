using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast;

public sealed partial class ChangeTokenFactory<T> : IChangeTokenFactory
{
#pragma warning disable IDE1006 // Naming Styles
    private readonly ISet<string> MemoryAcceptedEvents = new HashSet<string>([KnownEventTypes.CacheRemoved, KnownEventTypes.CacheRefreshed], StringComparer.InvariantCultureIgnoreCase);
    private readonly ISerializerProxy<T> _serializer;
#pragma warning restore IDE1006 // Naming Styles

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChangeTokenFactory<T>> _logger;
    private readonly Uri? _sourceUri;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly IKeyMaskingPolicy? _keyMaskingPolicy;

    public ChangeTokenFactory(IOptions<CacheOptions> optionsAccessor, ISerializerProxy<T> serializer, ILoggerFactory loggerFactory, ICachingTelemetryProvider telemetryProvider)
        : this(optionsAccessor, serializer, loggerFactory, telemetryProvider, keyMaskingPolicy: null)
    {
    }

    public ChangeTokenFactory(IOptions<CacheOptions> optionsAccessor, ISerializerProxy<T> serializer, ILoggerFactory loggerFactory, ICachingTelemetryProvider telemetryProvider, IKeyMaskingPolicy? keyMaskingPolicy)
    {
        _keyMaskingPolicy = keyMaskingPolicy;
        _sourceUri = optionsAccessor.Value.SourceUri;
        _serializer = serializer;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChangeTokenFactory<T>>();
        _telemetryProvider = telemetryProvider;
    }

    public ICacheChangeToken Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType)
    {
        var masker = KeyMasker.For(_keyMaskingPolicy, cacheName);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            LogCreateChangeToken(topic.TopicKey, LoggedKey.For(masker, token), _sourceUri);
        }

        var acceptedEvents = KnownCacheProviderNames.InMemory.Equals(cacheName, StringComparison.OrdinalIgnoreCase) ? MemoryAcceptedEvents : null;
        return new ChangeToken<T>(token, topic, _sourceUri, _serializer, _loggerFactory.CreateLogger<ChangeToken<T>>(), _telemetryProvider, acceptedEvents, masker);
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Create change token. topic {TopicKey} token {Token} source {SourceUri}")]
    private partial void LogCreateChangeToken(TopicKey topicKey, LoggedKey token, Uri? sourceUri);
}
