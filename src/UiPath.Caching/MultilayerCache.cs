using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal sealed partial class MultilayerCache : MultilayerCacheBase, ICache, ISpanKeyCache, IResultCache, IMigrationCache
{
    private readonly ICache _innerCache;

    private readonly bool _fencedInner;

    private readonly bool _sharedInner;

    private readonly InFlight<InFlightKey, object?> _generations = new();

    private readonly Lock _batchReserveGate = new();

    private readonly CacheEntryBuilder _entryBuilder;

    private readonly LocalMemorySetter _localMemorySetter;

    public MultilayerCache(
        string cacheName,
        ICache innerCache,
        IMemoryCacheFactory memoryCacheFactory,
        IChangeTokenFactory changeTokenFactory,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ICachingTelemetryProvider telemetryProvider,
        IMultilayerCacheOptions multiLayerCacheOptions,
        IMemoryCacheOptions memoryCacheOptions,
        CacheOptions cacheOptions,
        IDistributedLock distributedLock,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        ILogger logger,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
        : base(cacheName, innerCache, memoryCacheFactory, topicFactory, cacheEventFactory, telemetryProvider, multiLayerCacheOptions, memoryCacheOptions, cacheOptions, distributedLock, policyFactory, clock, logger, keyMaskingPolicy)
    {
        _innerCache = innerCache;
        _fencedInner = innerCache is IFencedFillTier { IsFenced: true };
        var cacheKeyStrategy = _multiLayerCacheOptions.CacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        var topicKeyStrategy = _multiLayerCacheOptions.TopicKeyStrategy ?? new DefaultTopicKeyStrategy(cacheOptions.Separator);
        _entryBuilder = new CacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _localMemorySetter = new LocalMemorySetter(cacheName, changeTokenFactory, _topicProvider, _memoryCache, logger, _clock, _multiLayerCacheOptions, memoryCacheOptions, telemetryProvider, _masker, !_clearLocalOnReconnect)
        {
            Invalidations = _innerTier.Shared,
            RepeatInvalidation = _repeats is { } repeats ? repeats.Schedule : null,
            PeerRefresh = _peerRefresher is { } refresher ? refresher.TryEnqueue : null,
            PeerRefreshChanged = _peerRefresher is { } changed ? changed.TokenChanged : null,
            // A keep that lands while a peer's event is dispatched is heard by the entry's token, not by the store.
            TokenChanged = (name, entry) => StaleOrNull?.Drop(name, entry),
        };
        _sharedInner = _innerTier.IsShared;
    }

    /// <summary>The tier told of received invalidations: the inner cache when it tracks them; tests check the wiring.</summary>
    internal ISharedTier? Invalidations => _localMemorySetter.Invalidations;

    /// <summary>Runs, inner reads and generators fail-safe stopped waiting for, still going; tests check they drain once callers are done.</summary>
    internal int RunsInFlight => _innerReads.Count + _generations.Count + _background.InFlight + (_peerRefresher?.Pending ?? 0) + LateGenerations;
}
