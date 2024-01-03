namespace UiPath.Platform.Caching;

internal class HashLocalMemorySetter : MemoryCacheSetter
{

    public HashLocalMemorySetter(
        string cacheName,
        IChangeTokenFactory changeTokenFactory,
        ITopicProvider topicProvider,
        IMemoryCache memoryCache,
        ILogger logger,
        CacheClock clock,
        IMultilayerCacheOptions cacheOptions)
        :base(cacheName, changeTokenFactory, topicProvider, memoryCache, logger, clock, cacheOptions)
    {
    }

    protected override ICacheEntryOptions CreateEntry(RefreshMetadataState metadataState, CancellationToken cancellationToken)
    {
        var token = metadataState.Token;
        return new InternalHashCacheEntryOptions
        {
            CacheKey = metadataState.CacheKey,
            TopicKey = metadataState.TopicKey,
            Token = cancellationToken,
            Expiration = Clock.ToDateTimeOffset(token.Expiration),
            Metadata = token.Metadata,
        };
    }
}
