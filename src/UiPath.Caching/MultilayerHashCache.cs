using System.Collections.Immutable;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal sealed partial class MultilayerHashCache : MultilayerCacheBase, IHashCache, ISpanKeyHashCache
{
    // A hash that stays a write short for long, such as when a write of this node was never laid over it, must not hold every write since.
    private const int MaxOwnWritesAhead = 32;

    private readonly Lock[] _mergeLocks = Enumerable.Range(0, 64).Select(static _ => new Lock()).ToArray();
    private readonly IHashCache _innerCache;
    private readonly InFlight<InFlightKey, object?> _generations = new();
    private readonly HashCacheEntryBuilder _entryBuilder;
    private readonly HashLocalMemorySetter _localMemorySetter;
    private readonly bool _sharedInner;
    private readonly ISharedHashTier? _versioned;
    private readonly PeerFieldLimits _peerLimits;

    // Held copies that lack a write, which a peer's update is not merged over: one handed to the refresh for a peer's write, marked under
    // the key's merge lock, and one this node is dropping, marked before it goes.
    private readonly ConditionalWeakTable<object, object> _awaitingRefresh = [];

    public MultilayerHashCache(
        string cacheName,
        IHashCache innerCache,
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
        var cacheKeyStrategy = _multiLayerCacheOptions.CacheKeyStrategy ?? new DefaultCacheKeyStrategy();
        var topicKeyStrategy = _multiLayerCacheOptions.TopicKeyStrategy ?? new DefaultTopicKeyStrategy(cacheOptions.Separator);
        _entryBuilder = new HashCacheEntryBuilder(cacheKeyStrategy, topicKeyStrategy, _clock);
        _sharedInner = _innerTier.IsShared;
        if (_innerTier.Versioned is not null && _topicProvider is not NullTopicProvider)
        {
            _versioned = _innerTier.Versioned;
            _background.Activate();
            _peerLimits = _innerTier.PeerLimits;
        }

        _localMemorySetter = new HashLocalMemorySetter(cacheName, changeTokenFactory, _topicProvider, _memoryCache, logger, _clock, _multiLayerCacheOptions, memoryCacheOptions, telemetryProvider, _masker, !_clearLocalOnReconnect)
        {
            Invalidations = _innerTier.Shared,
            RepeatInvalidation = _repeats is { } repeats ? repeats.Schedule : null,
            PeerApply = _versioned is null ? null : TryApplyPeerUpdate,
            PeerRefresh = _peerRefresher is null ? null : HandToRefresh,
            PeerRefreshChanged = _peerRefresher is { } changed ? changed.TokenChanged : null,
        };
    }

    /// <summary>The tier told of received invalidations: the inner cache when it tracks them; tests check the wiring.</summary>
    internal ISharedTier? Invalidations => _localMemorySetter.Invalidations;

    /// <summary>Test seam: runs on the dispatcher thread as a peer's write is handed to the refresh, before the key is queued.</summary>
    internal Action? PeerRefreshHandOver { get; set; }

    /// <summary>Runs and inner reads still in the tables; tests check they drain once callers are done.</summary>
    internal int RunsInFlight => _innerReads.Count + _generations.Count + _background.InFlight + (_peerRefresher?.Pending ?? 0);

    private bool OnlyTier => !_sharedInner;

    private static bool IsNullOrEmpty<T>(IDictionary<string, T?>? value) =>
        value is null || value.Count == 0;

    private static ImmutableDictionary<string, T?> Empty<T>() =>
        ImmutableDictionary<string, T?>.Empty;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache missed. generating new {CacheKey}")]
    private partial void LogCacheMissed(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replacing cached cacheKey {CacheKey}")]
    private partial void LogReplacingCachedKey(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing cached. cacheKey {CacheKey}")]
    private partial void LogClearingCached(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refreshing inner cache cacheKey {CacheKey} at expiration {Expiration}")]
    private partial void LogRefreshingInnerCacheKey(LoggedKey cacheKey, DateTimeOffset? expiration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache refresh value for cacheKey {CacheKey}")]
    private partial void LogInnerCacheRefreshError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache contains for cacheKey {CacheKey}")]
    private partial void LogInnerCacheContainsError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Set metadata for cacheKey {CacheKey}")]
    private partial void LogSetMetadata(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set metadata for cacheKey {CacheKey} failed")]
    private partial void LogInnerCacheSetMetadataFailed(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clearing local cached. cacheKey {CacheKey}")]
    private partial void LogClearingLocalCached(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache remove cacheKey {CacheKey}")]
    private partial void LogInnerCacheRemoveError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found local. {CacheKey}")]
    private partial void LogFoundLocal(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Using local copy for cacheKey {CacheKey}")]
    private partial void LogUsingLocalCopyDisconnected(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Returning default for cacheKey {CacheKey}")]
    private partial void LogReturningDefaultDisconnected(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found inner copy at cacheKey {CacheKey}")]
    private partial void LogFoundInnerCopy(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A peer update to {CacheKey} could not be applied; the local copy is refreshed or dropped.")]
    private partial void LogPeerUpdateFailed(Exception ex, LoggedKey cacheKey);
}
