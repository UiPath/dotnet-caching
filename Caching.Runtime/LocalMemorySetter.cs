using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

internal class LocalMemorySetter : MemoryCacheSetter
{
    public LocalMemorySetter(
        string cacheName,
        IChangeTokenFactory changeTokenFactory,
        ITopicProvider topicProvider,
        IMemoryCache memoryCache,
        ILogger logger,
        CacheClock clock,
        IMultilayerCacheOptions cacheOptions,
        ICachingTelemetryProvider telemetryProvider)
        : base(cacheName, changeTokenFactory, topicProvider, memoryCache, logger, clock, cacheOptions, telemetryProvider)
    {
    }

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
