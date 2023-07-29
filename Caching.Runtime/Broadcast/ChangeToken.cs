namespace UiPath.Platform.Caching.Broadcast;

public sealed class ChangeToken : ICacheChangeToken, IObserver<ICacheEvent>, IDisposable
{
    private readonly string _key;
    private readonly TopicKey _topic;
    private readonly Uri? _source;
    private readonly ILogger<ChangeToken> _logger;
    private readonly ISet<string>? _acceptedEvents;
    private readonly IDisposable _unsubscriber;

    private readonly List<(Action<object?> callback, object? state)> _callbacks = new();

    public ChangeToken(string key, ITopic<ICacheEvent> topic, Uri? source, ILogger<ChangeToken> logger, ISet<string>? acceptedEvents = null)
    {
        _key = key;
        _topic = topic.TopicKey;
        _source = source;
        _logger = logger;
        _acceptedEvents = acceptedEvents;
        _logger.LogTrace("Waiting from message {} on topic {}", _key, _topic);
        _unsubscriber = topic.Subscribe(this);
    }

    public bool HasChanged { get; private set; }

    public bool MetadataHasChanged { get; private set; }

    public bool ActiveChangeCallbacks => true;

    public DateTimeOffset? Expiration { get; private set; }

    public IDictionary<string, string?>? Metadata  { get; private set; }

    public void OnCompleted() =>
        _logger.LogTrace("OnCompleted {},{}", _key, _topic);

    public void OnError(Exception error)
    {
        _logger.LogDebug(error, "Clear local cache {},{}", _key, _topic);
        Notify();
    }

    public void OnNext(ICacheEvent cacheEvent)
    {
        var data = cacheEvent.Data;
        if (IsAcceptedEvent(cacheEvent))
        {
            _logger.LogDebug("Clear local cache key {}. Topic:{}, Id {}, Source:{}", _key, _topic, cacheEvent.Id, cacheEvent.Source);
            Notify(data);
        }
        else
        {
            _logger.LogDebug("Event ignored. Key {}, Topic:{}, Id {}, Source:{}", data?.Key, _topic, cacheEvent.Id, cacheEvent.Source);
        }
    }

    private void Notify(CacheEventData? data = default)
    {
        HasChanged = true;
        if(data!= null && data.Properties != null)
        {
            if (data.Properties.TryGetValue(KnownFieldNames.ExpirationKey, out object? dt) && dt is DateTimeOffset datetime)
            {
                Expiration = datetime;
                MetadataHasChanged = true;
            }

            if (data.Properties.TryGetValue(KnownFieldNames.MetadataKey, out object? m) && m is IDictionary<string, string?> mt)
            {
                Metadata = mt;
                MetadataHasChanged = true;
            }
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
            _logger.LogDebug("Event ignored. Key {}, Topic:{}, Id {}, Source:{}", data?.Key, _topic, cacheEvent.Id, cacheEvent.Source);
            return false;
        }

        if (Uri.Compare(_source, cacheEvent.Source, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.InvariantCultureIgnoreCase) == 0)
        {
            _logger.LogDebug("Event ignored. Topic:{}, Id {}, Source:{}", _topic, cacheEvent.Id, cacheEvent.Source);
            return false;
        }

        if (_acceptedEvents != null && !_acceptedEvents.Contains(cacheEvent.Type!))
        {
            _logger.LogDebug("Event ignored. Type{} Topic:{}, Id {}, Source:{}", cacheEvent.Type, _topic, cacheEvent.Id, cacheEvent.Source);
            return false;
        }

        return true;
    }

    public void Dispose() =>
        _unsubscriber?.Dispose();
}
