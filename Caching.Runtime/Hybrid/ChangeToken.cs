using UiPath.Platform.Caching.Broadcast;
using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.Hybrid;

public sealed class ChangeToken : IExtendedPropertiesChangeToken, IObserver<IClearCacheEvent>, IDisposable
{
    private readonly string _key;
    private readonly Channel _channel;
    private readonly Uri? _source;
    private readonly ILogger<ChangeToken> _logger;
    private readonly IDisposable _unsubscriber;

    private readonly List<(Action<object?> callback, object? state)> _callbacks = new();

    public ChangeToken(string key, Channel channel, IChannelSubscriber<IClearCacheEvent> subscriber, Uri? source, ILogger<ChangeToken> logger)
    {
        _key = key;
        _channel = channel;
        _source = source;
        _logger = logger;
        _logger.LogTrace("Waiting from redis message {} on channel {}", _key, _channel);
        _unsubscriber = subscriber.Subscribe(channel, this);
    }

    public bool HasChanged { get; private set; }

    public bool ExtendedPropertiesHasChanged { get; private set; }

    public bool ActiveChangeCallbacks => true;

    public void OnCompleted() =>
        _logger.LogTrace("OnCompleted {},{}", _key, _channel);

    public void OnError(Exception error)
    {
        _logger.LogDebug(error, "Clear local cache {},{}", _key, _channel);
        Notify();
    }

    public void OnNext(IClearCacheEvent cloudEvent)
    {

        var data = cloudEvent.Data;

        if (!string.Equals(data?.Key, _key, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogTrace("Event ignored. Key {}, Channel:{}, Id {}, Source:{}", data?.Key, _channel, cloudEvent.Id, cloudEvent.Source);
            return;
        }

        if (Uri.Compare(_source, cloudEvent.Source, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.InvariantCultureIgnoreCase) == 0)
        {
            _logger.LogTrace("Event ignored. Channel:{}, Id {}, Source:{}", _channel, cloudEvent.Id, cloudEvent.Source);
            return;
        }

        _logger.LogDebug("Clear local cache key {}. Channel:{}, Id {}, Source:{}", _key, _channel, cloudEvent.Id, cloudEvent.Source);
        Notify(data);
    }

    private void Notify(ClearCacheEventData? data = default)
    {
        HasChanged = true;
        ExtendedPropertiesHasChanged = data?.Fields?.Contains(CacheConstants.ExtendedPropertiesKey) ?? false;
        _callbacks.ForEach(kv => kv.callback(kv.state));
    }

    public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
    {
        _callbacks.Add(new(callback, state));
        return this;
    }

    public void Dispose() =>
        _unsubscriber?.Dispose();
}
