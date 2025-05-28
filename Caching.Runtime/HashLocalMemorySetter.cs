namespace UiPath.Platform.Caching;

internal class HashLocalMemorySetter(
    string cacheName,
    IChangeTokenFactory changeTokenFactory,
    ITopicProvider topicProvider,
    IMemoryCache memoryCache,
    ILogger logger,
    CacheClock clock,
    IMultilayerCacheOptions cacheOptions,
    IMemoryCacheOptions memoryCacheOptions,
    Telemetry.ICachingTelemetryProvider telemetryProvider)
    : MemoryCacheSetter(cacheName, changeTokenFactory, topicProvider, memoryCache, logger, clock, cacheOptions, memoryCacheOptions, telemetryProvider)
{
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
