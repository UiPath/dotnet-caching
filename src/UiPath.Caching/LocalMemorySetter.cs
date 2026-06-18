using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal class LocalMemorySetter(
    string cacheName,
    IChangeTokenFactory changeTokenFactory,
    ITopicProvider topicProvider,
    IMemoryCache memoryCache,
    ILogger logger,
    CacheClock clock,
    IMultilayerCacheOptions cacheOptions,
    IMemoryCacheOptions memoryCacheOptions,
    ICachingTelemetryProvider telemetryProvider)
    : MemoryCacheSetter(cacheName, changeTokenFactory, topicProvider, memoryCache, logger, clock, cacheOptions, memoryCacheOptions, telemetryProvider)
{
    protected override ICacheEntryOptions CreateEntry(RefreshMetadataState metadataState, CancellationToken cancellationToken)
    {
        var token = metadataState.Token;
        return new CacheEntryOptions
        {
            CacheKey = metadataState.CacheKey,
            TopicKey = metadataState.TopicKey,
            Token = cancellationToken,
            Expiration = Clock.ToDateTimeOffset(token.Expiration),
            Metadata = token.Metadata,
        };
    }
}
