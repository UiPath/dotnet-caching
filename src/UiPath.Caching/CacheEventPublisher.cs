namespace UiPath.Caching;

public sealed partial class CacheEventPublisher
{
    private readonly string _cacheName;
    private readonly ITopicProvider _topicProvider;
    private readonly ICacheEventFactory _cacheEventFactory;
    private readonly ILogger _logger;
    private readonly KeyMasking _keyMasking;

    public CacheEventPublisher(
        string cacheName,
        ITopicProvider topicProvider,
        ICacheEventFactory cacheEventFactory,
        ILogger logger)
        : this(cacheName, topicProvider, cacheEventFactory, logger, KeyMasking.Off)
    {
    }

    internal CacheEventPublisher(
        string cacheName,
        ITopicProvider topicProvider,
        ICacheEventFactory cacheEventFactory,
        ILogger logger,
        KeyMasking keyMasking)
    {
        _cacheName = cacheName;
        _topicProvider = topicProvider;
        _cacheEventFactory = cacheEventFactory;
        _logger = logger;
        _keyMasking = keyMasking;
    }

    private LoggedKey Logged(CacheKey cacheKey) => new(cacheKey.Name, layerPrefix: null, _keyMasking);

    public ValueTask<bool> MetadataUpdatedAsync(ICacheEntryOptions options)
    {
        Dictionary<string, object?> properties = new()
        {
            [KnownFieldNames.MetadataKey] = options.Metadata,
            [KnownFieldNames.ExpirationKey] = options.Expiration,
        };
        return RaiseEventAsync(options.TopicKey, options.CacheKey, KnownEventTypes.CacheRefreshed, properties);
    }

    public ValueTask<bool> CacheSetAsync(ICacheEntryOptions options) =>
        RaiseEventAsync(options.TopicKey, options.CacheKey, KnownEventTypes.CacheSet);

    public ValueTask<bool> CacheRefreshedAsync(ICacheEntryOptions options)
    {
        Dictionary<string, object?> properties = new()
        {
            [KnownFieldNames.ExpirationKey] = options.Expiration,
        };
        return RaiseEventAsync(options.TopicKey, options.CacheKey, KnownEventTypes.CacheRemoved, properties);
    }

    public ValueTask<bool> CacheRemovedAsync(ICacheEntryOptions options) =>
        RaiseEventAsync(options.TopicKey, options.CacheKey, KnownEventTypes.CacheRemoved);

    private async ValueTask<bool> RaiseEventAsync(TopicKey topicKey, CacheKey cacheKey, string eventType, IDictionary<string, object?>? properties = null)
    {
        LogRaiseEvent(eventType, topicKey, cacheKey);
        var data = new CacheEventData(cacheKey)
        {
            Properties = properties
        };
        var ev = _cacheEventFactory.Create(_cacheName, eventType, data);
        var topic = _topicProvider.Create(topicKey);
        return await topic.PublishAsync(ev, CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Raise {EventType} on topicKey {TopicKey} for key {CacheKey}")]
    private partial void LogRaiseEventCore(string eventType, TopicKey topicKey, LoggedKey cacheKey);

    // The generated methods take a LoggedKey, so a raw key cannot reach them without going through LogKeys.
#pragma warning disable CA1873 // LoggedKey defers the digest to ToString, which the generated method calls only when the level is enabled
    private void LogRaiseEvent(string eventType, TopicKey topicKey, CacheKey cacheKey) => LogRaiseEventCore(eventType, topicKey, Logged(cacheKey));
#pragma warning restore CA1873
}
