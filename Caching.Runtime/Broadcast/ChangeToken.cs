namespace UiPath.Platform.Caching.Broadcast;

public sealed class ChangeToken : ICacheChangeToken, IObserver<ICacheEvent>, IDisposable
{
    private readonly string _key;
    private readonly TopicKey _topic;
    private readonly Uri? _source;
    private readonly ISerializerProxy _serializer;
    private readonly ILogger<ChangeToken> _logger;
    private readonly ISet<string>? _acceptedEvents;
    private readonly IDisposable _unsubscriber;

    private readonly List<(Action<object?> callback, object? state)> _callbacks = [];

    public ChangeToken(string key, ITopic<ICacheEvent> topic, Uri? source, ISerializerProxy serializer, ILogger<ChangeToken> logger, ISet<string>? acceptedEvents = null)
    {
        _key = key;
        _topic = topic.TopicKey;
        _source = source;
        _serializer = serializer;
        _logger = logger;
        _acceptedEvents = acceptedEvents;
        _logger.LogTrace("Waiting from message {Key} on topic {Topic}", _key, _topic);
        _unsubscriber = topic.Subscribe(this);
    }

    public bool HasChanged { get; private set; }

    public bool MetadataHasChanged { get; private set; }

    public bool ActiveChangeCallbacks => true;

    public DateTimeOffset? Expiration { get; private set; }

    public IDictionary<string, string?>? Metadata  { get; private set; }

    public void OnCompleted() =>
        _logger.LogTrace("OnCompleted {Key},{Topic}", _key, _topic);

    public void OnError(Exception error)
    {
        _logger.LogDebug(error, "Clear local cache {Key},{Topic}", _key, _topic);
        Notify();
    }

    public void OnNext(ICacheEvent cacheEvent)
    {
        var data = cacheEvent.Data;
        if (IsAcceptedEvent(cacheEvent))
        {
            _logger.LogDebug("Clear local cache key {Key}. Topic:{Topic}, Id {EventId}, Source:{EventSource}", _key, _topic, cacheEvent.Id, cacheEvent.Source);
            Notify(data);
        }
        else
        {
            _logger.LogDebug("Event ignored. Key {Key}, Topic:{Topic}, Id {EventId}, Source:{EventSource}", data?.Key, _topic, cacheEvent.Id, cacheEvent.Source);
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
            _logger.LogDebug("Event ignored. Key {Key}, Topic:{Topic}, Id {EventId}, Source:{EventSource}", data?.Key, _topic, cacheEvent.Id, cacheEvent.Source);
            return false;
        }

        if (Uri.Compare(_source, cacheEvent.Source, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.InvariantCultureIgnoreCase) == 0)
        {
            _logger.LogDebug("Event ignored. Topic:{Topic}, Id {EventId}, Source:{EventSource}", _topic, cacheEvent.Id, cacheEvent.Source);
            return false;
        }

        if (_acceptedEvents != null && !_acceptedEvents.Contains(cacheEvent.Type!))
        {
            _logger.LogDebug("Event ignored. Type:{EventType} Topic:{Topic}, Id {EventId}, Source:{EventSource}", cacheEvent.Type, _topic, cacheEvent.Id, cacheEvent.Source);
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
}
