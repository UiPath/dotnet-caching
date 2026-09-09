using System.Collections.Concurrent;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast;

public sealed partial class ChangeTokenFactory<T> : IChangeTokenFactory, IMaskedChangeTokenFactory
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
    private readonly ConcurrentDictionary<string, KeyMasker> _maskers = new(StringComparer.Ordinal);

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

    public ICacheChangeToken Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType) =>
        CreateCore(token, topic, cacheName, entryType, _maskers.GetOrAdd(cacheName, name => KeyMasker.For(_keyMaskingPolicy, name)), callerKey: token);

    /// <summary>A tier built with its own policy, such as a private cache behind the distributed adapter, passes the masker it was given.</summary>
    ICacheChangeToken IMaskedChangeTokenFactory.Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType, KeyMasker masker, CacheKey callerKey) =>
        CreateCore(token, topic, cacheName, entryType, masker, callerKey);

    private ChangeToken<T> CreateCore(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType, KeyMasker masker, CacheKey callerKey)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            LogCreateChangeToken(topic.TopicKey, LoggedKey.For(masker, callerKey, token, entryType), _sourceUri);
        }

        var acceptedEvents = KnownCacheProviderNames.InMemory.Equals(cacheName, StringComparison.OrdinalIgnoreCase) ? MemoryAcceptedEvents : null;
        return new ChangeToken<T>(token, topic, _sourceUri, _serializer, _loggerFactory.CreateLogger<ChangeToken<T>>(), _telemetryProvider, acceptedEvents, masker, entryType, callerKey);
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Create change token. topic {TopicKey} token {Token} source {SourceUri}")]
    private partial void LogCreateChangeToken(TopicKey topicKey, LoggedKey token, Uri? sourceUri);
}
