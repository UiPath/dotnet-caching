using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast;

public sealed partial class ChangeToken<T> : ICacheChangeToken, IKeyedObserver<ICacheEvent>, IMissedEventsObserver, IDisposable
{
    private readonly string _key;
    private readonly KeyMasker _masker;
    private readonly Type? _entryType;
    private readonly CacheKey _callerKey;
    private readonly TopicKey _topic;
    private readonly Uri? _source;
    private readonly ISerializerProxy<T> _serializer;
    private readonly ILogger<ChangeToken<T>> _logger;
    private readonly ISet<string>? _acceptedEvents;
    private readonly IDisposable _unsubscriber;
    private readonly ICachingTelemetryProvider _telemetryProvider;

    // Set for a cache that turned ClearLocalOnReconnect off: it keeps entries over a subscription gap, but not over a known loss.
    private readonly bool _ignoreSubscriptionGaps;

    private readonly List<(Action<object?> callback, object? state)> _callbacks = [];

    // An event and a loss report can arrive on different threads; each notification and registration takes its turn.
#if NET9_0_OR_GREATER
    private readonly Lock _notifyLock = new();
#else
    private readonly object _notifyLock = new();
#endif

    // Set once a removal, a loss or an error expired the entry: a refresh the dispatcher delivers after it would re-insert the old value.
    private bool _expired;

    public ChangeToken(
        string key,
        ITopic<ICacheEvent> topic,
        Uri? source,
        ISerializerProxy<T> serializer,
        ILogger<ChangeToken<T>> logger,
        ICachingTelemetryProvider telemetryProvider,
        ISet<string>? acceptedEvents = null)
        : this(key, topic, source, serializer, logger, telemetryProvider, acceptedEvents, KeyMasker.Off, entryType: null, callerKey: key)
    {
    }

    internal ChangeToken(
        string key,
        ITopic<ICacheEvent> topic,
        Uri? source,
        ISerializerProxy<T> serializer,
        ILogger<ChangeToken<T>> logger,
        ICachingTelemetryProvider telemetryProvider,
        ISet<string>? acceptedEvents,
        KeyMasker masker,
        Type? entryType,
        CacheKey callerKey,
        bool ignoreSubscriptionGaps = false)
    {
        _ignoreSubscriptionGaps = ignoreSubscriptionGaps;
        _masker = masker;
        _entryType = entryType;
        _callerKey = callerKey;
        _key = key;
        _topic = topic.TopicKey;
        _source = source;
        _serializer = serializer;
        _logger = logger;
        _acceptedEvents = acceptedEvents;
        LogWaitingForMessage(Logged(), _topic);
        _telemetryProvider = telemetryProvider;
        _unsubscriber = topic.Subscribe(this);
    }

    public bool HasChanged { get; private set; }

    public bool MetadataHasChanged { get; private set; }

    public bool ActiveChangeCallbacks => true;

    public DateTimeOffset? Expiration { get; private set; }

    public IDictionary<string, string?>? Metadata  { get; private set; }

    public string? TransportId { get; private set; }

    string IKeyedObserver<ICacheEvent>.Key => _key;

    public void OnCompleted() =>
        LogOnCompleted(Logged(), _topic);

    public void OnError(Exception error)
    {
        LogClearLocalCacheOnError(error, Logged(), _topic);
        Notify();
    }

    public void OnNext(ICacheEvent cacheEvent)
    {
        var data = cacheEvent.Data;
        if (IsAcceptedEvent(cacheEvent))
        {
            TransportId = cacheEvent.TransportId;
            LogClearLocalCacheKey(Logged(), _topic, cacheEvent.Id, cacheEvent.Source);
            Notify(data);
            _telemetryProvider.TrackTopicReadMetric(_topic, TransportId);
        }
        else
        {
            // No key to name when the event carried no data; saying so beats logging an empty one.
            if (data?.Key is { } ignoredKey)
            {
                LogEventIgnoredWithKey(LoggedForeign(ignoredKey), _topic, cacheEvent.Id, cacheEvent.Source);
            }
            else
            {
                LogEventIgnored(_topic, cacheEvent.Id, cacheEvent.Source);
            }

            _telemetryProvider.TrackTopicReadMetric(_topic, cacheEvent.TransportId);
        }
    }

    void IMissedEventsObserver.OnEventsMissed(MissedEventsReason reason)
    {
        if (reason == MissedEventsReason.SubscriptionGap && _ignoreSubscriptionGaps)
        {
            return;
        }

        LogClearLocalCacheOnLoss(Logged(), _topic);
        Notify();
    }



    public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
    {
        lock (_notifyLock)
        {
            // A token can change while it subscribes, before its holder registers: a late registration runs at once.
            if (HasChanged)
            {
                callback(state);
            }
            else
            {
                _callbacks.Add(new(callback, state));
            }
        }

        return this;
    }

    public void Dispose() =>
        _unsubscriber?.Dispose();

    /// <summary>This token's own key: the caller's, rendered inside the composed key it subscribes with.</summary>
    private LoggedKey Logged() => LoggedKey.For(_masker, _callerKey, _key, _entryType);

    /// <summary>A key off the wire has no caller key to judge, so it is masked whole when masking is on.</summary>
    private LoggedKey LoggedForeign(string key) => LoggedKey.Composed(_masker, key, _entryType);

    private void Notify(CacheEventData? data = default)
    {
        lock (_notifyLock)
        {
            HasChanged = true;
            if (data?.Properties != null && !_expired)
            {
                ExtractExpiration(data.Properties);
                ExtractMetadata(data.Properties);
            }
            else
            {
                // A removal or a loss carries nothing to refresh with: what an earlier refresh left would re-insert the value.
                _expired = true;
                Expiration = null;
                Metadata = null;
                MetadataHasChanged = false;
            }

            _callbacks.ForEach(kv => kv.callback(kv.state));
        }
    }

    private bool IsAcceptedEvent(ICacheEvent cacheEvent)
    {
        var data = cacheEvent.Data;

        if (!string.Equals(data?.Key, _key, StringComparison.OrdinalIgnoreCase))
        {
            // No key to name when the event carried no data; saying so beats logging an empty one.
            if (data?.Key is { } ignoredKey)
            {
                LogEventIgnoredWithKey(LoggedForeign(ignoredKey), _topic, cacheEvent.Id, cacheEvent.Source);
            }
            else
            {
                LogEventIgnored(_topic, cacheEvent.Id, cacheEvent.Source);
            }

            return false;
        }

        if (Uri.Compare(_source, cacheEvent.Source, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.InvariantCultureIgnoreCase) == 0)
        {
            LogEventIgnored(_topic, cacheEvent.Id, cacheEvent.Source);
            return false;
        }

        if (_acceptedEvents != null && !_acceptedEvents.Contains(cacheEvent.Type!))
        {
            LogEventIgnoredWithType(cacheEvent.Type, _topic, cacheEvent.Id, cacheEvent.Source);
            return false;
        }

        return true;
    }

    private void ExtractMetadata(IDictionary<string, object?> properties)
    {
        if (!properties.TryGetValue(KnownFieldNames.MetadataKey, out object? m) || m is null)
        {
            return;
        }

        if (m is IDictionary<string, string?> mt || _serializer.TryDeserialize(m, out mt!))
        {
            Metadata = mt;
            MetadataHasChanged = true;
        }
    }

    private void ExtractExpiration(IDictionary<string, object?> properties)
    {
        if (!properties.TryGetValue(KnownFieldNames.ExpirationKey, out object? dt) || dt is null)
        {
            return;
        }

        if (dt is DateTimeOffset datetime || _serializer.TryDeserialize(dt, out datetime))
        {
            Expiration = datetime;
            MetadataHasChanged = true;
        }
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Waiting for message {Key} on topic {Topic}")]
    private partial void LogWaitingForMessage(LoggedKey key, TopicKey topic);

    [LoggerMessage(Level = LogLevel.Trace, Message = "OnCompleted {Key},{Topic}")]
    private partial void LogOnCompleted(LoggedKey key, TopicKey topic);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clear local cache {Key},{Topic}")]
    private partial void LogClearLocalCacheOnError(Exception error, LoggedKey key, TopicKey topic);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clear local cache {Key}: topic {Topic} lost invalidations")]
    private partial void LogClearLocalCacheOnLoss(LoggedKey key, TopicKey topic);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clear local cache key {Key}. Topic:{Topic}, Id {EventId}, Source:{EventSource}")]
    private partial void LogClearLocalCacheKey(LoggedKey key, TopicKey topic, string? eventId, Uri? eventSource);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Event ignored. Key {Key}, Topic:{Topic}, Id {EventId}, Source:{EventSource}")]
    private partial void LogEventIgnoredWithKey(LoggedKey key, TopicKey topic, string? eventId, Uri? eventSource);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Event ignored. Topic:{Topic}, Id {EventId}, Source:{EventSource}")]
    private partial void LogEventIgnored(TopicKey topic, string? eventId, Uri? eventSource);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Event ignored. Type:{EventType} Topic:{Topic}, Id {EventId}, Source:{EventSource}")]
    private partial void LogEventIgnoredWithType(string? eventType, TopicKey topic, string? eventId, Uri? eventSource);
}
