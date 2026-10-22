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
        return RaiseEventAsync(options, KnownEventTypes.CacheRefreshed, entryType, properties);
    }

    public ValueTask<bool> CacheRemovedAsync(ICacheEntryOptions options) =>
        CacheRemovedAsync(options, entryType: null);

    /// <inheritdoc cref="MetadataUpdatedAsync(ICacheEntryOptions, Type?)"/>
    public ValueTask<bool> CacheRemovedAsync(ICacheEntryOptions options, Type? entryType) =>
        RaiseEventAsync(options, KnownEventTypes.CacheRemoved, entryType);

    /// <summary>For the caches' own option structs, which come in as they are rather than boxed into the interface.</summary>
    internal ValueTask<bool> MetadataUpdatedAsync<TOptions>(TOptions options, Type? entryType)
        where TOptions : struct, ICacheEntryOptions
    {
        Dictionary<string, object?> properties = new()
        {
            [KnownFieldNames.MetadataKey] = options.Metadata,
            [KnownFieldNames.ExpirationKey] = options.Expiration,
        };
        return RaiseEventAsync(options, KnownEventTypes.CacheRefreshed, entryType, properties);
    }

    /// <inheritdoc cref="MetadataUpdatedAsync{TOptions}(TOptions, Type?)"/>
    internal ValueTask<bool> CacheSetAsync<TOptions>(TOptions options, Type? entryType)
        where TOptions : struct, ICacheEntryOptions =>
        RaiseEventAsync(options, KnownEventTypes.CacheSet, entryType);

    /// <inheritdoc cref="MetadataUpdatedAsync{TOptions}(TOptions, Type?)"/>
    internal ValueTask<bool> CacheRefreshedAsync<TOptions>(TOptions options, Type? entryType)
        where TOptions : struct, ICacheEntryOptions
    {
        Dictionary<string, object?> properties = new()
        {
            [KnownFieldNames.ExpirationKey] = options.Expiration,
        };
        return RaiseEventAsync(options, KnownEventTypes.CacheRefreshed, entryType, properties);
    }

    /// <inheritdoc cref="MetadataUpdatedAsync{TOptions}(TOptions, Type?)"/>
    internal ValueTask<bool> CacheRemovedAsync<TOptions>(TOptions options, Type? entryType)
        where TOptions : struct, ICacheEntryOptions =>
        RaiseEventAsync(options, KnownEventTypes.CacheRemoved, entryType);

    internal ValueTask<bool> TopicInvalidatedAsync(TopicKey topicKey)
    {
        try
        {
            if (_topicProvider is NullTopicProvider)
            {
                return new ValueTask<bool>(true);
            }

            var ev = _cacheEventFactory.Create(_cacheName, KnownEventTypes.TopicInvalidated, new CacheEventData(KnownFieldNames.AllKeys));
            return _topicProvider.Create(topicKey).PublishAsync(ev, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<bool>(ex);
        }
    }

    private ValueTask<bool> RaiseEventAsync<TOptions>(TOptions options, string eventType, Type? entryType, IDictionary<string, object?>? properties = null)
        where TOptions : ICacheEntryOptions
    {
        // Not async: nothing is awaited before the publish, whose task is handed back as it is; a failure before it is reported through the task, as the async method reported it.
        try
        {
            var topicKey = options.TopicKey;
            var cacheKey = options.CacheKey;
            LogRaiseEvent(eventType, topicKey, LoggedKey.For(_masker, options.CallerKey, cacheKey.Name, entryType));
            if (_topicProvider is NullTopicProvider)
            {
                // Broadcast is off: its topic takes anything and keeps nothing, so the event is not built.
                return new ValueTask<bool>(true);
            }

            var data = new CacheEventData(cacheKey)
            {
                Properties = properties,
            };
            var ev = _cacheEventFactory.Create(_cacheName, eventType, data);
            return _topicProvider.Create(topicKey).PublishAsync(ev, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<bool>(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Raise {EventType} on topicKey {TopicKey} for key {CacheKey}")]
    private partial void LogRaiseEvent(string eventType, TopicKey topicKey, LoggedKey cacheKey);
}
