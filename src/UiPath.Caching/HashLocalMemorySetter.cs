namespace UiPath.Caching;

internal sealed class HashLocalMemorySetter(
    string cacheName,
    IChangeTokenFactory changeTokenFactory,
    ITopicProvider topicProvider,
    IMemoryCache memoryCache,
    ILogger logger,
    TimeProvider clock,
    IMultilayerCacheOptions cacheOptions,
    IMemoryCacheOptions memoryCacheOptions,
    Telemetry.ICachingTelemetryProvider telemetryProvider,
    KeyMasker? masker = null,
    bool ignoreSubscriptionGaps = false)
    : MemoryCacheSetter(cacheName, changeTokenFactory, topicProvider, memoryCache, logger, clock, cacheOptions, memoryCacheOptions, telemetryProvider, masker, ignoreSubscriptionGaps)
{
    internal Func<RefreshMetadataState, PeerUpdate, bool>? PeerApply { get; set; }

    protected override bool AppliesPeerUpdates => PeerApply is not null;

    protected override bool TryApplyPeerUpdate(RefreshMetadataState metadataState, PeerUpdate update) =>
        PeerApply?.Invoke(metadataState, update) ?? false;

    protected override void Renewed(ICacheEntry held, ICacheEntry renewed) =>
        HashVersions.Carry(held, renewed);

    protected override ICacheEntryOptions CreateEntry(RefreshMetadataState metadataState, CancellationToken cancellationToken)
    {
        var token = metadataState.Token;
        return new InternalHashCacheEntryOptions
        {
            CacheKey = metadataState.CacheKey,
            CallerKey = metadataState.CallerKey,
            TopicKey = metadataState.TopicKey,
            Token = cancellationToken,
            Expiration = Clock.ToDateTimeOffset(token.Expiration),
            Metadata = token.Metadata,
        };
    }
}
