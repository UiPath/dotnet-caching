namespace UiPath.Platform.Caching;

internal sealed class CacheEventPublisher
{
    private readonly string _cacheName;
    private readonly string? _topicProviderName;
    private readonly ITopicFactory _topicFactory;
    private readonly ICacheEventFactory _cacheEventFactory;
    private readonly ILogger _logger;

    public CacheEventPublisher(
        string cacheName,
        string? topicProviderName,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ILogger logger)
    {
        _cacheName = cacheName;
        _topicProviderName = topicProviderName;
        _topicFactory = topicFactory;
        _cacheEventFactory = cacheEventFactory;
        _logger = logger;
    }

    public Task MetadataUpdatedAsync<T>(ICacheEntryOptions options)
    {
        Dictionary<string, object?> properties = new()
        {
            [KnownFieldNames.MetadataKey] = options.Metadata,
            [KnownFieldNames.ExpirationKey] = options.Expiration,
        };
        return RaiseEventAsync<T>(options.TopicKey, options.CacheKey, KnownEventTypes.CacheRefreshed, properties);
    }

    public Task CacheSetAsync<T>(ICacheEntryOptions options) =>
        RaiseEventAsync<T>(options.TopicKey, options.CacheKey, KnownEventTypes.CacheSet);

    public Task CacheRefreshedAsync<T>(ICacheEntryOptions options)
    {
        Dictionary<string, object?> properties = new()
        {
            [KnownFieldNames.ExpirationKey] = options.Expiration,
        };
        return RaiseEventAsync<T>(options.TopicKey, options.CacheKey, KnownEventTypes.CacheRemoved, properties);
    }

    public Task CacheRemovedAsync<T>(ICacheEntryOptions options) =>
        RaiseEventAsync<T>(options.TopicKey, options.CacheKey, KnownEventTypes.CacheRemoved);

    private async Task RaiseEventAsync<T>(TopicKey topicKey, CacheKey cacheKey, string eventType, IDictionary<string, object?>? properties = null)
    {
        _logger.LogDebug("Raise {} on topicKey {} for key {}", eventType, topicKey, cacheKey);
        var data = new CacheEventData(cacheKey)
        {
            Properties = properties
        };
        var ev = _cacheEventFactory.Create(_cacheName, eventType, data);
        var topic = _topicFactory.Get<T>(_topicProviderName, topicKey);
        await topic.PublishAsync(ev, CancellationToken.None).ConfigureAwait(false);
    }
}
