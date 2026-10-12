using System.Collections.Concurrent;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast;

public sealed partial class ChangeTokenFactory<T> : IChangeTokenFactory, IMaskedChangeTokenFactory
{
#pragma warning disable IDE1006, SX1309 // reads as a constant, so it keeps its PascalCase name
    private readonly ISet<string> MemoryAcceptedEvents = new HashSet<string>([KnownEventTypes.CacheRemoved, KnownEventTypes.CacheRefreshed], StringComparer.OrdinalIgnoreCase);
    private readonly ISerializerProxy<T> _serializer;
#pragma warning restore IDE1006, SX1309

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
        _logger = loggerFactory.CreateLogger<ChangeTokenFactory<T>>();
        TokenLogger = loggerFactory.CreateLogger<ChangeToken<T>>();
        _telemetryProvider = telemetryProvider;
    }

    // One logger for every token: resolving one builds its category name, per token otherwise. A property, since the log generator wants one logger field.
    [SuppressMessage("Minor Code Smell", "S6669:Logger field or property name should comply with a naming convention", Justification = "The factory's own logger is _logger; this one is handed to every token it creates.")]
    private ILogger<ChangeToken<T>> TokenLogger { get; }

    public ICacheChangeToken Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType) =>
        CreateCore(token, topic, cacheName, entryType, _maskers.GetOrAdd(cacheName, name => KeyMasker.For(_keyMaskingPolicy, name)), callerKey: token);

    /// <summary>A tier built with its own policy, such as a private cache behind the distributed adapter, passes the masker it was given.</summary>
    ICacheChangeToken IMaskedChangeTokenFactory.Create(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType, KeyMasker masker, CacheKey callerKey, bool ignoreSubscriptionGaps) =>
        CreateCore(token, topic, cacheName, entryType, masker, callerKey, ignoreSubscriptionGaps);

    private ChangeToken<T> CreateCore(string token, ITopic<ICacheEvent> topic, string cacheName, Type entryType, KeyMasker masker, CacheKey callerKey, bool ignoreSubscriptionGaps = false)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            LogCreateChangeToken(topic.TopicKey, LoggedKey.For(masker, callerKey, token, entryType), _sourceUri);
        }

        var acceptedEvents = KnownCacheProviderNames.InMemory.Equals(cacheName, StringComparison.OrdinalIgnoreCase) ? MemoryAcceptedEvents : null;
        return new ChangeToken<T>(token, topic, _sourceUri, _serializer, TokenLogger, _telemetryProvider, acceptedEvents, masker, entryType, callerKey, ignoreSubscriptionGaps);
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Create change token. topic {TopicKey} token {Token} source {SourceUri}")]
    private partial void LogCreateChangeToken(TopicKey topicKey, LoggedKey token, Uri? sourceUri);
}
