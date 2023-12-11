namespace UiPath.Platform.Caching;

internal abstract class MemoryCacheSetter
{
    protected string CacheName { get; set; }
    protected IChangeTokenFactory ChangeTokenFactory { get; set; }
    protected ITopicFactory TopicFactory { get; set; }
    protected IMemoryCache MemoryCache { get; set; }
    protected ILogger Logger { get; set; }
    protected IMultilayerCacheOptions CacheOptions { get; set; }
    protected CacheClock Clock { get; set; }

    protected MemoryCacheSetter(
        string cacheName,
        IChangeTokenFactory changeTokenFactory,
        ITopicFactory topicFactory,
        IMemoryCache memoryCache,
        ILogger logger,
        CacheClock clock,
        IMultilayerCacheOptions cacheOptions)
    {
        CacheName = cacheName;
        ChangeTokenFactory = changeTokenFactory;
        TopicFactory = topicFactory;
        MemoryCache = memoryCache;
        Logger = logger;
        Clock = clock;
        CacheOptions = cacheOptions;
    }

    public void Set(ICacheEntryOptions options, ICacheEntry item, Type entryType, TimeSpan? maxExpiration)
    {
        var topic = TopicFactory.Get(CacheOptions.Topic, options.TopicKey, entryType);
        var token = ChangeTokenFactory.Create(options.CacheKey, topic, CacheName, entryType);

        var state = new RefreshMetadataState(options.CacheKey, options.TopicKey, item, token, entryType, maxExpiration);
        token.RegisterChangeCallback(RefreshMetadata, state);
        var memOptions = new MemoryCacheEntryOptions();
        var expiration = GetCacheExpiration(options.Expiration, maxExpiration);
        memOptions.SetAbsoluteExpiration(expiration);
        memOptions.ExpirationTokens.Add(token);
        memOptions.RegisterPostEvictionCallback(PostEviction, token);
        MemoryCache.Set(options.CacheKey, item, memOptions);
    }

    static void PostEviction(object key, object? value, EvictionReason reason, object? state)
    {
        if (state is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void RefreshMetadata(object? state)
    {
        if (state is RefreshMetadataState metadataState)
        {
            RefreshMetadata(metadataState);
        }
    }

    private void RefreshMetadata(RefreshMetadataState metadataState)
    {
        var token = metadataState.Token;
        if (!token.MetadataHasChanged && token.Expiration == null)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(CacheOptions.Timeout);
            var options = CreateEntry(metadataState, cts.Token);
            var newEntry = metadataState.CacheEntity.NewEntry(options.Expiration, options.Metadata);
            Set(options, newEntry, metadataState.EntryType, metadataState.MaxExpiration);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Unable to refresh cache cacheKey {}", metadataState.CacheKey);
        }
    }

    protected abstract ICacheEntryOptions CreateEntry(RefreshMetadataState metadataState, CancellationToken cancellationToken);

    private DateTimeOffset GetCacheExpiration(DateTimeOffset expiration, TimeSpan? maxExpiration)
    {
        if (maxExpiration.HasValue)
        {
            var maxExp = Clock.UtcNow.Add(maxExpiration.Value);
            if (expiration > maxExp)
            {
                return maxExp;
            }
        }

        return expiration;
    }
}
