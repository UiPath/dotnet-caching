using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Broadcast;

public sealed partial class ChangeToken<T> : ICacheChangeToken, IKeyedObserver<ICacheEvent>, IDisposable
{
    private readonly string _key;
    private readonly TopicKey _topic;
    private readonly Uri? _source;
    private readonly ISerializerProxy<T> _serializer;
    private readonly ILogger<ChangeToken<T>> _logger;
    private readonly ISet<string>? _acceptedEvents;
    private readonly IDisposable _unsubscriber;
    private readonly ICachingTelemetryProvider _telemetryProvider;

    private readonly List<(Action<object?> callback, object? state)> _callbacks = [];

    public ChangeToken(
        string key,
        ITopic<ICacheEvent> topic,
        Uri? source,
        ISerializerProxy<T> serializer,
        ILogger<ChangeToken<T>> logger,
        ICachingTelemetryProvider telemetryProvider,
        ISet<string>? acceptedEvents = null)
    {
        _key = key;
        _topic = topic.TopicKey;
        _source = source;
        _serializer = serializer;
        _logger = logger;
        _acceptedEvents = acceptedEvents;
        LogWaitingForMessage(_key, _topic);
        _telemetryProvider = telemetryProvider;
        _unsubscriber = topic.Subscribe(this);
    }

    public bool HasChanged { get; private set; }

    public bool MetadataHasChanged { get; private set; }

    string IKeyedObserver<ICacheEvent>.Key => _key;

    public bool ActiveChangeCallbacks => true;

    public DateTimeOffset? Expiration { get; private set; }

    public IDictionary<string, string?>? Metadata  { get; private set; }

    public string? TransportId { get; private set; }

    public void OnCompleted() =>
        LogOnCompleted(_key, _topic);

    public void OnError(Exception error)
    {
        LogClearLocalCacheOnError(error, _key, _topic);
        Notify();
    }

    public void OnNext(ICacheEvent cacheEvent)
    {
        var data = cacheEvent.Data;
        if (IsAcceptedEvent(cacheEvent))
        {
            TransportId = cacheEvent.TransportId;
            LogClearLocalCacheKey(_key, _topic, cacheEvent.Id, cacheEvent.Source);
            Notify(data);
            _telemetryProvider.TrackTopicReadMetric(_topic, TransportId);
        }
        else
        {
            LogEventIgnoredWithKey(data?.Key, _topic, cacheEvent.Id, cacheEvent.Source);
            _telemetryProvider.TrackTopicReadMetric(_topic, cacheEvent.TransportId);
        }
    }

    private void Notify(CacheEventData? data = default)
    {
        HasChanged = true;
        if(data?.Properties != null)
        {
            ExtractExpiration(data.Properties);
            ExtractMetadata(data.Properties);
        }

        _callbacks.ForEach(kv => kv.callback(kv.state));
    }



    public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
    {
        _callbacks.Add(new(callback, state));
        return this;
    }

    private bool IsAcceptedEvent(ICacheEvent cacheEvent)
    {
        var data = cacheEvent.Data;

        if (!string.Equals(data?.Key, _key, StringComparison.OrdinalIgnoreCase))
        {
            LogEventIgnoredWithKey(data?.Key, _topic, cacheEvent.Id, cacheEvent.Source);
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

    public void Dispose() =>
        _unsubscriber?.Dispose();

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

    [LoggerMessage(Level = LogLevel.Trace, Message = "Waiting from message {Key} on topic {Topic}")]
    private partial void LogWaitingForMessage(string key, TopicKey topic);

    [LoggerMessage(Level = LogLevel.Trace, Message = "OnCompleted {Key},{Topic}")]
    private partial void LogOnCompleted(string key, TopicKey topic);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clear local cache {Key},{Topic}")]
    private partial void LogClearLocalCacheOnError(Exception error, string key, TopicKey topic);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clear local cache key {Key}. Topic:{Topic}, Id {EventId}, Source:{EventSource}")]
    private partial void LogClearLocalCacheKey(string key, TopicKey topic, string? eventId, Uri? eventSource);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Event ignored. Key {Key}, Topic:{Topic}, Id {EventId}, Source:{EventSource}")]
    private partial void LogEventIgnoredWithKey(string? key, TopicKey topic, string? eventId, Uri? eventSource);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Event ignored. Topic:{Topic}, Id {EventId}, Source:{EventSource}")]
    private partial void LogEventIgnored(TopicKey topic, string? eventId, Uri? eventSource);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Event ignored. Type:{EventType} Topic:{Topic}, Id {EventId}, Source:{EventSource}")]
    private partial void LogEventIgnoredWithType(string? eventType, TopicKey topic, string? eventId, Uri? eventSource);
}
