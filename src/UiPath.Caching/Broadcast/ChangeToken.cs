using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast;

public sealed partial class ChangeToken<T> : ICacheChangeToken, IKeyedObserver<ICacheEvent>, IMissedEventsObserver, IPeerUpdateSource, IInvalidationSource, IPeerRefreshSource, IDisposable
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
    private readonly Lock _notifyLock = new();

    // Set once a removal, a loss or an error expired the entry: a refresh the dispatcher delivers after it would re-insert the old value.
    private bool _expired;

    private Func<PeerUpdate, bool>? _peerUpdateHandler;

    private bool _eventsLost;

    private Action<object?, bool>? _invalidating;

    private object? _invalidatingState;

    private Func<object?, bool>? _peerRefresh;

    private object? _peerRefreshState;

    // Set once the refresh hook took a peer's write: the entry stays, and a refresh event must not rebuild it.
    private bool _refreshPending;

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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2292:Trivial properties should be auto-implemented", Justification = "An explicit implementation of an internal interface member cannot be an auto-property.")]
    Func<PeerUpdate, bool>? IPeerUpdateSource.PeerUpdateHandler
    {
        get => _peerUpdateHandler;
        set => _peerUpdateHandler = value;
    }

    string IKeyedObserver<ICacheEvent>.Key => _key;

    public void OnCompleted() =>
        LogOnCompleted(Logged(), _topic);

    public void OnError(Exception error)
    {
        LogClearLocalCacheOnError(error, Logged(), _topic);
        Volatile.Write(ref _eventsLost, true);
        Notify();
    }

    public void OnNext(ICacheEvent cacheEvent)
    {
        var data = cacheEvent.Data;
        if (IsAcceptedEvent(cacheEvent))
        {
            TransportId = cacheEvent.TransportId;
            LogClearLocalCacheKey(Logged(), _topic, cacheEvent.Id, cacheEvent.Source);
            Notify(data, cacheEvent.Type);
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
        Volatile.Write(ref _eventsLost, true);
        Notify();
    }



    void IInvalidationSource.RegisterInvalidatingCallback(Action<object?, bool> callback, object? state)
    {
        lock (_notifyLock)
        {
            // The state first: Notify reads the callback without the lock, and must see its state with it.
            _invalidatingState = state;
            Volatile.Write(ref _invalidating, callback);
            if (HasChanged)
            {
                callback(state, _eventsLost);
            }
        }
    }

    void IPeerRefreshSource.RegisterPeerRefresh(Func<object?, bool> refresh, object? state)
    {
        lock (_notifyLock)
        {
            // The state first: Notify reads the hook without the lock, and must see its state with it.
            _peerRefreshState = state;
            Volatile.Write(ref _peerRefresh, refresh);
        }
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

    private static bool CarriesWriteFields(IDictionary<string, object?> properties) =>
        properties.ContainsKey(KnownFieldNames.VersionKey) || properties.ContainsKey(KnownFieldNames.PeerFieldsKey);

    /// <summary>This token's own key: the caller's, rendered inside the composed key it subscribes with.</summary>
    private LoggedKey Logged() => LoggedKey.For(_masker, _callerKey, _key, _entryType);

    /// <summary>A key off the wire has no caller key to judge, so it is masked whole when masking is on.</summary>
    private LoggedKey LoggedForeign(string key) => LoggedKey.Composed(_masker, key, _entryType);

    private void Notify(CacheEventData? data = default, string? type = null)
    {
        // Before the token changes: a changed token reads as expired, and a reader that refills the entry from the tier below must find the key guarded.
        var invalidating = Volatile.Read(ref _invalidating);
        invalidating?.Invoke(_invalidatingState, Volatile.Read(ref _eventsLost));

        var peerWrite = string.Equals(type, KnownEventTypes.CacheSet, StringComparison.OrdinalIgnoreCase)
            && (data?.Properties is null || CarriesWriteFields(data.Properties));
        var peerRefresh = Volatile.Read(ref _peerRefresh);
        var pending = Volatile.Read(ref _refreshPending);

        if (TryHandPeerWrite(data, peerWrite, peerRefresh, pending))
        {
            return;
        }

        lock (_notifyLock)
        {
            // Registered while this ran: told here, still before the token changes.
            if (invalidating is null && _invalidating is { } late)
            {
                late(_invalidatingState, _eventsLost);
            }

            HasChanged = true;
            // A write that carried fields and was not applied is a removal to this copy, as every write was before fields were carried.
            // A pending token drops a refresh event too: it would rebuild the pre-write value with a fresh expiry.
            if (data?.Properties != null && !pending && !_expired && !CarriesWriteFields(data.Properties))
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

    /// <summary>Whether the update was applied or the refresh took the write, so the token stays unchanged; anything else, or a declined write, drops the entry.</summary>
    private bool TryHandPeerWrite(CacheEventData? data, bool peerWrite, Func<object?, bool>? peerRefresh, bool pending)
    {
        if (pending)
        {
            // Marks the key dirty again.
            return peerWrite && HandToRefresh(peerRefresh!);
        }

        // Before the token changes, since a changed token reads as expired to the cache that holds its entry, and outside its lock: the
        // update takes the key's merge lock, and a merge holding that lock can be waiting for this one to register on the token.
        if (data?.Properties != null && !Volatile.Read(ref _expired) && _peerUpdateHandler is { } apply && ExtractPeerUpdate(data.Properties) is { } update && apply(update))
        {
            return true;
        }

        if (peerWrite && !Volatile.Read(ref _expired) && peerRefresh is not null && HandToRefresh(peerRefresh))
        {
            Volatile.Write(ref _refreshPending, true);
            return true;
        }

        return false;
    }

    /// <summary>A hook that throws declines: the entry is dropped, as a write that cannot be refreshed always was.</summary>
    private bool HandToRefresh(Func<object?, bool> refresh)
    {
        try
        {
            return refresh(_peerRefreshState);
        }
        catch (Exception ex)
        {
            LogPeerRefreshFailed(ex, Logged(), _topic);
            return false;
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

    private PeerUpdate? ExtractPeerUpdate(IDictionary<string, object?> properties)
    {
        if (!properties.TryGetValue(KnownFieldNames.VersionKey, out object? v) || v is null
            || !properties.TryGetValue(KnownFieldNames.PeerFieldsKey, out object? f) || f is null)
        {
            return null;
        }

        if ((v is long version || _serializer.TryDeserialize(v, out version))
            && version > 0
            && (f is IDictionary<string, string?> fields || _serializer.TryDeserialize(f, out fields!))
            && fields is { Count: > 0 })
        {
            return new PeerUpdate(version, fields, PeerExpiration(properties), PeerMetadata(properties));
        }

        return null;
    }

    private DateTimeOffset? PeerExpiration(IDictionary<string, object?> properties)
    {
        if (properties.TryGetValue(KnownFieldNames.PeerExpirationKey, out object? dt) && dt is not null
            && (dt is DateTimeOffset datetime || _serializer.TryDeserialize(dt, out datetime)))
        {
            return datetime;
        }

        return null;
    }

    private IDictionary<string, string?>? PeerMetadata(IDictionary<string, object?> properties)
    {
        if (properties.TryGetValue(KnownFieldNames.PeerMetadataKey, out object? m) && m is not null
            && (m is IDictionary<string, string?> metadata || _serializer.TryDeserialize(m, out metadata!)))
        {
            return metadata;
        }

        return null;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Clear local cache {Key}: topic {Topic} could not refresh it after a peer's write")]
    private partial void LogPeerRefreshFailed(Exception error, LoggedKey key, TopicKey topic);

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
