using System.Runtime.CompilerServices;
using UiPath.Caching.Broadcast.Redis;
using UiPath.Caching.Config;
using UiPath.Caching.Locking;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

public abstract partial class MultilayerCacheBase : IDisposable
{
    protected readonly ILogger _logger;
    protected readonly IMemoryCache _memoryCache;
    protected readonly ICacheEntryFactory _cacheEntryFactory;
    protected readonly IMultilayerCacheOptions _multiLayerCacheOptions;
    protected readonly IDisposable _monitor;
    protected readonly TimeProvider _clock;
    protected readonly CacheEventPublisher _eventPublisher;
    protected readonly IConnectionState _connectionState;
    protected readonly ITopicProvider _topicProvider;
    protected readonly bool _useLocalOnlyWhenDisconnected;
    private protected readonly bool _clearLocalOnReconnect;
    private protected readonly RehydrationCoordinator _rehydrator;
    private protected readonly CachePolicy _defaultPolicy;
    private protected readonly InFlight<InFlightKey, object?> _innerReads = new();
    private protected readonly KeyMasker _masker;
    private protected readonly BackgroundWrites _background;
    private protected readonly InnerTier _innerTier;

    // Hardcoded fallback values for the lock fields. Merged in as the lowest-priority policy
    // (after provider-specific + user DefaultCachePolicy) so every cache instance has a fully
    // resolved Lock — every field non-null — by the time validation runs.
    // 
    // DistributedExpiration is deliberately not floored here: the floor is a write-side rule
    // (ResolveWriteDuration), and leaving it out keeps "nothing configured" observable as null.
    private static readonly CachePolicy HardcodedDefaults = new()
    {
        RefreshLocalOnPeerWrite = true,
        Lock = new LockProfile
        {
            LocalSingleFlightEnabled = true,
            DistributedLockEnabled = false,
            LocalSingleFlightTimeout = TimeSpan.FromMilliseconds(500),
            DistributedLockTimeout = TimeSpan.FromMilliseconds(500),
            DistributedLockExpiry = TimeSpan.FromSeconds(5),
        },
    };
    private readonly PeerWriteGuard? _peerWrites;
    private readonly IDistributedLock _distributedLock;
    private readonly IDistributedLockKeyStrategy _lockKeyStrategy;
    private readonly TimeSpan _distributedLockExpiry;
    private readonly TimeSpan _distributedLockTimeout;
    private readonly TimeSpan _localLockTimeout;
    private readonly bool _localLockEnabled;
    private readonly bool _distributedLockEnabled;
    private bool _disposed;

    protected MultilayerCacheBase(
        string cacheName,
        object innerCache,
        IMemoryCacheFactory memoryCacheFactory,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ICachingTelemetryProvider telemetryProvider,
        IMultilayerCacheOptions multiLayerCacheOptions,
        IMemoryCacheOptions memoryOptions,
        CacheOptions cacheOptions,
        IDistributedLock distributedLock,
        ICachePolicyFactory policyFactory,
        TimeProvider clock,
        ILogger logger,
        IKeyMaskingPolicy? keyMaskingPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _innerTier = InnerTier.For(innerCache, multiLayerCacheOptions);
        _masker = KeyMasker.For(keyMaskingPolicy, cacheName);
        _logger = logger;
        _multiLayerCacheOptions = multiLayerCacheOptions;
        _defaultPolicy = CachePolicyMerger.Merge(
            CachePolicyMerger.Merge(CachePolicyFromMultilayerOptions.Build(multiLayerCacheOptions), policyFactory.Default),
            HardcodedDefaults);
        CachePolicyFactoryValidator.ValidateAgainstEffectiveDefault(policyFactory, cacheOptions.DistributedLockPollInterval, _defaultPolicy);
        _memoryCache = memoryCacheFactory.Get(memoryOptions);
        Telemetry = telemetryProvider;
        _cacheEntryFactory = _multiLayerCacheOptions.EntryFactory ?? new CacheEntryFactory();
        _monitor = _memoryCache.Monitor(multiLayerCacheOptions, Telemetry, GetType().Name);
        _clock = clock;
        var redisOptions = multiLayerCacheOptions as InMemoryRedisCacheOptions;
        var drainTimeout = redisOptions?.BackgroundWriteDrainTimeout ?? InMemoryRedisCacheOptions.DefaultBackgroundWriteDrainTimeout;
        if (drainTimeout != Timeout.InfiniteTimeSpan)
        {
            drainTimeout = drainTimeout < TimeSpan.Zero ? TimeSpan.Zero : BoundedTimeout(drainTimeout);
        }

        _background = new BackgroundWrites(
            _innerTier.IsShared ? Math.Max(0, redisOptions?.MaxBackgroundWrites ?? InMemoryRedisCacheOptions.DefaultMaxBackgroundWrites) : 0,
            drainTimeout,
            cacheName,
            logger,
            telemetryProvider,
            clock);
        BatchChunkSize = Math.Max(1, redisOptions?.BatchChunkSize ?? InMemoryRedisCacheOptions.DefaultBatchChunkSize);
        BatchConcurrentChunks = Math.Max(1, redisOptions?.BatchConcurrentChunks ?? InMemoryRedisCacheOptions.DefaultBatchConcurrentChunks);
        BatchTopicInvalidationThreshold = redisOptions?.BatchTopicInvalidationThreshold;
        _topicProvider = topicFactory.Get(_multiLayerCacheOptions.Topic);
        _eventPublisher = new CacheEventPublisher(cacheName, _topicProvider, cacheEventFactory, logger, _masker);
        var connectionMonitorEnabled = multiLayerCacheOptions.ConnectionMonitorEnabled ?? cacheOptions.ConnectionMonitorEnabled;
        _connectionState = connectionMonitorEnabled ? GetConnectionMonitor(_innerTier.Connection, _topicProvider) : NullConnectionStateMonitor.Instance;
        _useLocalOnlyWhenDisconnected = (multiLayerCacheOptions.UseLocalOnlyWhenDisconnected ?? true) && connectionMonitorEnabled;
        _clearLocalOnReconnect = (multiLayerCacheOptions.ClearLocalOnReconnect ?? true) && connectionMonitorEnabled;
        if (_innerTier.IsShared && _topicProvider is not NullTopicProvider)
        {
            _peerRefresher = new PeerRefresher(this);
            _peerWrites = new PeerWriteGuard(_background, _topicProvider, cacheOptions.SourceUri, clock, logger);
        }

        _distributedLock = distributedLock;
        _lockKeyStrategy = multiLayerCacheOptions.LockKeyStrategy ?? new DefaultDistributedLockKeyStrategy(cacheOptions.Separator);
        var defaultLock = _defaultPolicy.Lock!;
        _distributedLockExpiry = defaultLock.DistributedLockExpiry!.Value;
        _distributedLockTimeout = defaultLock.DistributedLockTimeout!.Value;
        _localLockTimeout = defaultLock.LocalSingleFlightTimeout!.Value;
        _localLockEnabled = defaultLock.LocalSingleFlightEnabled!.Value;
        _distributedLockEnabled = defaultLock.DistributedLockEnabled!.Value;
        Name = cacheName;
        _rehydrator = new RehydrationCoordinator(cacheName, _clock, distributedLock, _lockKeyStrategy, telemetryProvider, logger, _masker);
    }

    public string Name { get; }

    internal bool BackgroundWritesActive => _background.Active;

    internal bool FillsTracked => _background.Counting;

    protected ICachingTelemetryProvider Telemetry { get; }
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected static TimeSpan ApplyJitter(TimeSpan duration, TimeSpan? maxJitter)
    {
        // TimeSpan.MaxValue means "no TTL"; jittering it would clamp it under the sentinel and back onto EXPIRE.
        if (duration <= TimeSpan.Zero || duration == TimeSpan.MaxValue || maxJitter is not { } max || max <= TimeSpan.Zero)
        {
            return duration;
        }
        // Bounded under the sentinel, so the sum can neither overflow nor land on it; the clock fits it into the DateTime range.
        var bonusTicks = Random.Shared.NextInt64(Math.Min(max.Ticks, TimeSpan.MaxValue.Ticks - duration.Ticks));
        return duration + new TimeSpan(bonusTicks);
    }

    /// <summary>Runs <paramref name="runGeneratorAndStoreAsync"/>, under the key's distributed lock when one is enabled: the lock is taken, the cache read again, and the generator run only on a miss. With no distributed lock it runs the generator at once; de-duplicating callers of one process is the run table's job, not a lock's.</summary>
    protected async ValueTask<TResult> RunUnderLocksAsync<TResult>(
        CacheKey cacheKey,
        Func<ValueTask<TResult>> readCachedAsync,
        Func<TResult, bool> isHit,
        Func<CancellationToken, ValueTask<TResult>> runGeneratorAndStoreAsync,
        CancellationToken token,
        LockProfile? policyLock = null)
    {
        if (!DistributedLockOn(policyLock))
        {
            return await runGeneratorAndStoreAsync(token).ConfigureAwait(false);
        }

        await using var lease = await AcquireDistributedLockAsync(cacheKey, policyLock, token).ConfigureAwait(false);
        var fromCache = await readCachedAsync().ConfigureAwait(false);
        return isHit(fromCache) ? fromCache : await runGeneratorAndStoreAsync(token).ConfigureAwait(false);
    }

    protected Task<TResult> InvokeFactoryAsync<TResult>(
        CacheKey cacheKey,
        Func<CancellationToken, Task<TResult>> factory,
        TimeSpan? factoryTimeout,
        CancellationToken token) =>
        FactoryTimeout.RunAsync(factory, factoryTimeout, cacheKey, Name, Telemetry, token);

    /// <summary>The L2 write lifetime: a caller value as-is, else <see cref="ResolveDuration"/> jittered.</summary>
    protected TimeSpan ResolveWriteDuration(CachePolicy policy, TimeSpan? callerExpiration = null) =>
        callerExpiration ?? ApplyJitter(ResolveDuration(policy), policy.JitterMaxDuration);

    /// <summary>
    /// The configured L2 lifetime, floored and unjittered: policy → options default → <see cref="CachePolicy.DefaultDistributedExpiration"/>.
    /// The rehydrate threshold measures against this, so it cannot depend on a per-write draw.
    /// </summary>
    protected TimeSpan ResolveDuration(CachePolicy policy) =>
        policy.DistributedExpiration
        ?? _multiLayerCacheOptions.DefaultExpiration
        ?? CachePolicy.DefaultDistributedExpiration;

    protected bool GetInnerCacheDisconnected() => _useLocalOnlyWhenDisconnected && !_connectionState.IsConnected;

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _peerWrites?.Dispose();
                _background.Drain();
                _monitor.Dispose();
                _peerRefresher?.Dispose();
                _memoryCache.Dispose();
                if (_connectionState is IDisposable connectionState)
                {
                    connectionState.Dispose();
                }
            }
            _disposed = true;
        }
    }

    /// <summary>Gives <paramref name="to"/> the last read of the entry it replaces.</summary>
    private protected static void InheritRead(object? from, object? to)
    {
        if (from is IReadTracked { LastRead: not IReadTracked.NeverRead and var last } && to is IReadTracked inheriting)
        {
            inheriting.MarkRead(last);
        }
    }

    /// <summary>A timeout a cancellation source takes: <c>CancelAfter</c> throws beyond <see cref="int.MaxValue"/> milliseconds, so a misconfigured <c>Lock.LocalSingleFlightTimeout</c> degrades to that.</summary>
    private protected static TimeSpan BoundedTimeout(TimeSpan timeout) =>
        timeout.TotalMilliseconds > int.MaxValue ? TimeSpan.FromMilliseconds(int.MaxValue) : timeout;

    /// <summary>Notes a local hit, so a peer's write to the entry refreshes it rather than drops it while the read is recent.</summary>
    private protected void MarkRead(object? entry)
    {
        if (_peerRefresher is null)
        {
            return;
        }

        if (entry is IReadTracked tracked)
        {
            tracked.MarkRead(NowSeconds());
        }
    }

    /// <summary>Whole seconds of the cache's monotonic clock, at least 1.</summary>
    private protected int NowSeconds() => (int)(_clock.GetTimestamp() / _clock.TimestampFrequency) + 1;

    /// <summary>How long a hit read from the inner tier stays local: the disconnected cap while the tier reads as down, perhaps only its broadcast.</summary>
    private protected TimeSpan? LocalLifetime(CachePolicy policy) =>
        GetInnerCacheDisconnected()
            ? policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected
            : policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration;

    private protected Task DrainBackgroundAsync() => _background.DrainAsync();

    /// <summary>Taken before an inner read of the key whose value may be kept locally; peers' writes on <paramref name="topic"/> after it refuse the fill.</summary>
    private protected long MarkInnerRead(string key, TopicKey topic)
    {
        _peerWrites?.Watch(topic);
        return _background.Mark(key);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Inner copy of {CacheKey} not kept locally: a write of it, or of a key sharing its stripe, came after the read began.")]
    private protected partial void LogInnerCopyNotKept(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Inner cache is not connected. Setting local only for cacheKey {CacheKey}")]
    private protected partial void LogSettingLocalOnly(LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inner cache set value for {CacheKey}")]
    private protected partial void LogInnerCacheSetError(Exception ex, LoggedKey cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Set {CacheKey} is in the inner cache but its event was not published; other nodes keep their local copy until it expires.")]
    private protected partial void LogSetEventError(Exception ex, LoggedKey cacheKey);

    private protected bool ApplyLocal<TState>(string key, TState state, Func<TState, bool> change) => _background.Apply(key, state, change);

    private protected void RemoveLocal(string key) =>
        _background.Apply(key, (Memory: _memoryCache, Key: key), static s =>
        {
            s.Memory.Remove(s.Key);
            return true;
        });

    private protected TimeSpan? DisconnectedLifetime(CachePolicy policy) =>
        policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected;

    private protected TimeSpan? ConnectedLifetime(CachePolicy policy) =>
        policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration;

    /// <summary>Whether a newer write, a removal or an invalidation has not replaced the entry since.</summary>
    private protected bool IsLocalEntry(string name, object entry) =>
        _memoryCache.TryGetValue(name, out var current) && ReferenceEquals(current, entry);

    /// <summary>Write, then promote and publish; a failed step is counted and leaves the value local for the disconnected cap.</summary>
    private protected async ValueTask DetachedWriteAsync<TState>(TState state, Func<TState, CancellationToken, ValueTask<bool>> write, Action<TState> promote, Func<TState, ValueTask<bool>>? publish, CancellationToken token)
    {
        var written = false;
        try
        {
            written = await write(state, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A background write in {Cache} failed.", Name);
        }

        if (!written)
        {
            // The Redis setters answer a cancelled write with false; a drain's cancel is counted apart.
            token.ThrowIfCancellationRequested();

            _background.Failed("write");
            return;
        }

        try
        {
            promote(state);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A background write in {Cache} could not extend the local entry.", Name);
        }

        if (publish is not null && !await publish(state).ConfigureAwait(false))
        {
            _background.Failed("event");
        }
    }

    /// <summary>Writes locally after an inner write: for the normal lifetime when the inner tier took it, and with <paramref name="keepRefused"/> for the disconnected cap when it refused it.</summary>
    private protected bool KeepAfterInnerWrite<TState>(bool written, bool keepRefused, CachePolicy policy, TState state, Func<TState, TimeSpan?, bool> memorySet, CancellationToken token)
    {
        if (written)
        {
            return memorySet(state, policy.LocalExpiration ?? _multiLayerCacheOptions.LocalMaxExpiration);
        }

        // The Redis setters answer a canceled write with false, so the caller's token tells a refusal from a cancellation.
        if (keepRefused && !token.IsCancellationRequested)
        {
            // Kept as a disconnected write is kept, so the callers waiting on the local lock reuse it.
            memorySet(state, policy.LocalExpirationDisconnected ?? _multiLayerCacheOptions.LocalMaxExpirationDisconnected);
        }

        return false;
    }

    /// <summary>The key as a log line should show it. Nothing is rendered unless the line is written.</summary>
    private protected LoggedKey Logged(CacheKey key, Type? valueType = null) => LoggedKey.For(_masker, key, valueType);

    /// <summary>Shows the composed key but judges, and masks, the caller's own key inside it.</summary>
    private protected LoggedKey Logged(CacheEntryOptions options, Type? valueType = null) =>
        LoggedKey.For(_masker, options.CallerKey, options.CacheKey.Name, valueType);

    /// <inheritdoc cref="Logged(CacheEntryOptions, Type?)"/>
    private protected LoggedKeys Logged(IReadOnlyCollection<CacheEntryOptions> options, Type? valueType = null) => new(_masker, options, valueType);

    /// <inheritdoc cref="Logged(CacheEntryOptions, Type?)"/>
    private protected LoggedKey Logged(InternalHashCacheEntryOptions options, Type? valueType = null) =>
        LoggedKey.For(_masker, options.CallerKey, options.CacheKey.Name, valueType);

    /// <inheritdoc cref="Logged(CacheKey, Type?)"/>
    private protected LoggedKeys Logged(IReadOnlyCollection<CacheKey> keys, Type? valueType = null) => new(_masker, keys, valueType);

    /// <summary>A key the site only has in composed form; with no caller key to judge it is masked whole.</summary>
    private protected LoggedKey LoggedComposed(CacheKey composed, Type? valueType = null) =>
        LoggedKey.Composed(_masker, composed.Name, valueType);

    /// <inheritdoc cref="LoggedComposed(CacheKey, Type?)"/>
    private protected LoggedKeys LoggedComposed(IReadOnlyCollection<CacheKey> composed, Type? valueType = null) =>
        new(_masker, composed, valueType, composedOnly: true);

    /// <summary>
    /// Validates a caller-supplied duration and pairs it with the deadline it implies. The write path
    /// needs both: the deadline for the entry options, the duration for the L1 cap and the rehydrate
    /// trigger. Jitter is deliberately not applied — a caller-supplied lifetime is honored exactly.
    /// </summary>
    private protected (DateTimeOffset Expiration, TimeSpan Duration) CallerWrite(TimeSpan expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null)
    {
        var duration = CacheExpiration.ThrowIfNotPositive(expiration, paramName);
        return (_clock.ToDateTimeOffset(duration), duration);
    }

    /// <inheritdoc cref="CallerWrite(TimeSpan, string)"/>
    private protected (DateTimeOffset Expiration, TimeSpan Duration) CallerWrite(DateTimeOffset expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        (expiration, CacheExpiration.ToDuration(expiration, _clock.GetUtcNow(), paramName));

    /// <summary>Write expiration from the policy chain, jittered.</summary>
    private protected DateTimeOffset GetExpiration(CachePolicy policy) =>
        _clock.ToDateTimeOffset(ResolveWriteDuration(policy));

    /// <summary>Write expiration from an options object: <c>ExpireTime</c>, then <c>TimeToLive</c>, then the policy chain.</summary>
    private protected DateTimeOffset GetExpiration(HashCacheEntryOptions options, CachePolicy policy) =>
        options.ExpireTime ?? _clock.ToDateTimeOffset(options.TimeToLive ?? ResolveWriteDuration(policy));

    /// <summary>Write expiration for a caller-supplied duration, validated.</summary>
    private protected DateTimeOffset GetExpiration(TimeSpan expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        CallerWrite(expiration, paramName).Expiration;

    /// <summary>Write expiration for a caller-supplied instant, validated.</summary>
    private protected DateTimeOffset GetExpiration(DateTimeOffset expiration, [CallerArgumentExpression(nameof(expiration))] string? paramName = null) =>
        CacheExpiration.ThrowIfNotFuture(expiration, _clock.GetUtcNow(), paramName);

    /// <summary>
    /// One place for the in-process policy: a per-call <see cref="LockProfile"/> wins over the
    /// options. <c>Enabled</c> turns the shared run per key on; <c>Timeout</c> is how long a
    /// <c>TryAddAsync</c> waits for a run that holds its key. It bypasses the options validators,
    /// so the timeout falls back when out of range.
    /// </summary>
    private protected (bool Enabled, TimeSpan Timeout) ResolveLocalLock(LockProfile? policyLock) =>
        (policyLock?.LocalSingleFlightEnabled ?? _localLockEnabled,
         PositiveOrFallback(policyLock?.LocalSingleFlightTimeout, _localLockTimeout));

    /// <summary>Whether callers of one process share a run per key: <c>Lock.LocalSingleFlightEnabled</c>.</summary>
    private protected bool SharesRuns(LockProfile? policyLock) => ResolveLocalLock(policyLock).Enabled;

    private protected bool DistributedLockOn(LockProfile? policyLock) => policyLock?.DistributedLockEnabled ?? _distributedLockEnabled;

    /// <summary>The key's distributed lock, with the timeout and expiry of the call's profile. Per-call profiles bypass the options validators, so a value out of range falls back to the cache-wide one.</summary>
    private protected ValueTask<IAsyncDisposable> AcquireDistributedLockAsync(CacheKey cacheKey, LockProfile? policyLock, CancellationToken token) =>
        _distributedLock.AcquireAsync(
            _lockKeyStrategy.GetLockKey(cacheKey),
            PositiveOrFallback(policyLock?.DistributedLockExpiry, _distributedLockExpiry),
            NonNegativeOrFallback(policyLock?.DistributedLockTimeout, _distributedLockTimeout),
            token);

    private static TimeSpan PositiveOrFallback(TimeSpan? value, TimeSpan fallback) =>
        value is { } v && v > TimeSpan.Zero ? v : fallback;

    private static TimeSpan NonNegativeOrFallback(TimeSpan? value, TimeSpan fallback) =>
        value is { } v && v >= TimeSpan.Zero ? v : fallback;

    private IConnectionState GetConnectionMonitor(params object?[] connectionStates)
    {
        var lst = connectionStates.OfType<IConnectionState>().ToArray();
        return lst.Length == 0 ? NullConnectionStateMonitor.Instance : new ConnectionStateMonitor(Telemetry, _multiLayerCacheOptions.ConnectionMonitorPeriod ?? TimeSpan.FromSeconds(5), lst);
    }
}
