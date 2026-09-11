namespace UiPath.Caching;

public sealed partial class CacheEventPublisher
{
    private readonly string _cacheName;
    private readonly ITopicProvider _topicProvider;
    private readonly ICacheEventFactory _cacheEventFactory;
    private readonly ILogger _logger;
    private readonly KeyMasker _masker;

    public CacheEventPublisher(
        string cacheName,
        ITopicProvider topicProvider,
        ICacheEventFactory cacheEventFactory,
        ILogger logger)
        : this(cacheName, topicProvider, cacheEventFactory, logger, KeyMasker.Off)
    {
    }

    internal CacheEventPublisher(
        string cacheName,
        ITopicProvider topicProvider,
        ICacheEventFactory cacheEventFactory,
        ILogger logger,
        KeyMasker masker)
    {
        _masker = masker;
        _cacheName = cacheName;
        _topicProvider = topicProvider;
        _cacheEventFactory = cacheEventFactory;
        _logger = logger;
    }

    public ValueTask<bool> MetadataUpdatedAsync(ICacheEntryOptions options) =>
        MetadataUpdatedAsync(options, entryType: null);

    /// <summary><paramref name="entryType"/> is what the cache holds, for a masking policy that decides by value type.</summary>
    public ValueTask<bool> MetadataUpdatedAsync(ICacheEntryOptions options, Type? entryType)
    {
        Dictionary<string, object?> properties = new()
        {
            [KnownFieldNames.MetadataKey] = options.Metadata,
            [KnownFieldNames.ExpirationKey] = options.Expiration,
        };
        return RaiseEventAsync(options, KnownEventTypes.CacheRefreshed, entryType, properties);
    }

    public ValueTask<bool> CacheSetAsync(ICacheEntryOptions options) =>
        CacheSetAsync(options, entryType: null);

    /// <inheritdoc cref="MetadataUpdatedAsync(ICacheEntryOptions, Type?)"/>
    public ValueTask<bool> CacheSetAsync(ICacheEntryOptions options, Type? entryType) =>
        RaiseEventAsync(options, KnownEventTypes.CacheSet, entryType);

    public ValueTask<bool> CacheRefreshedAsync(ICacheEntryOptions options) =>
        CacheRefreshedAsync(options, entryType: null);

    /// <inheritdoc cref="MetadataUpdatedAsync(ICacheEntryOptions, Type?)"/>
    public ValueTask<bool> CacheRefreshedAsync(ICacheEntryOptions options, Type? entryType)
    {
        Dictionary<string, object?> properties = new()
        {
            [KnownFieldNames.ExpirationKey] = options.Expiration,
        };
        return RaiseEventAsync(options, KnownEventTypes.CacheRemoved, entryType, properties);
    }

    public ValueTask<bool> CacheRemovedAsync(ICacheEntryOptions options) =>
        CacheRemovedAsync(options, entryType: null);

    /// <inheritdoc cref="MetadataUpdatedAsync(ICacheEntryOptions, Type?)"/>
    public ValueTask<bool> CacheRemovedAsync(ICacheEntryOptions options, Type? entryType) =>
        RaiseEventAsync(options, KnownEventTypes.CacheRemoved, entryType);

    private async ValueTask<bool> RaiseEventAsync(ICacheEntryOptions options, string eventType, Type? entryType, IDictionary<string, object?>? properties = null)
    {
        var topicKey = options.TopicKey;
        var cacheKey = options.CacheKey;
        LogRaiseEvent(eventType, topicKey, LoggedKey.For(_masker, options.CallerKey, cacheKey.Name, entryType));
        var data = new CacheEventData(cacheKey)
        {
            Properties = properties,
        };
        var ev = _cacheEventFactory.Create(_cacheName, eventType, data);
        var topic = _topicProvider.Create(topicKey);
        return await topic.PublishAsync(ev, CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Raise {EventType} on topicKey {TopicKey} for key {CacheKey}")]
    private partial void LogRaiseEvent(string eventType, TopicKey topicKey, LoggedKey cacheKey);
}
