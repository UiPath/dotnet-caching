using UiPath.Platform.Caching.Locking;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

public abstract class MultilayerCacheBase : IDisposable
{
    private bool _disposed;
    protected readonly ILogger _logger;
    protected readonly IMemoryCache _memoryCache;
    protected readonly ICacheEntryFactory _cacheEntryFactory;
    protected readonly IMultilayerCacheOptions _multiLayerCacheOptions;
    protected readonly IDisposable _monitor;
    protected readonly CacheClock _clock;
    protected readonly CacheEventPublisher _eventPublisher;
    protected readonly IConnectionState _connectionState;
    protected readonly ITopicProvider _topicProvider;
    protected readonly bool _useLocalOnlyWhenDisconnected;
    private readonly ILocalLock _localLock;
    private readonly IDistributedLock _distributedLock;
    private readonly IDistributedLockKeyStrategy _lockKeyStrategy;
    private protected readonly RehydrationCoordinator _rehydrator;
    private readonly string _localLockKeyPrefix;
    private readonly TimeSpan _distributedLockExpiry;
    private readonly TimeSpan _distributedLockTimeout;
    private readonly TimeSpan _localLockTimeout;
    private readonly bool _localLockEnabled;
    private readonly bool _distributedLockEnabled;
    private protected readonly ICachePolicyFactory _policyFactory;
    private static readonly TimeSpan ClockDriftSlack = TimeSpan.FromMinutes(1);

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
        ILocalLock localLock,
        IDistributedLock distributedLock,
        ICachePolicyFactory policyFactory,
        ILogger logger)
    {
        _logger = logger;
        _multiLayerCacheOptions = multiLayerCacheOptions;
        ValidateExpirationOptions(multiLayerCacheOptions);
        ValidateLockOptions(multiLayerCacheOptions);
        _memoryCache = memoryCacheFactory.Get(memoryOptions);
        Telemetry = telemetryProvider;
        _cacheEntryFactory = _multiLayerCacheOptions.EntryFactory ?? new CacheEntryFactory();
        _monitor = _memoryCache.Monitor(multiLayerCacheOptions, Telemetry, GetType().Name);
        _clock = new CacheClock(_multiLayerCacheOptions.Clock, _multiLayerCacheOptions.DefaultExpiration);
        _topicProvider = topicFactory.Get(_multiLayerCacheOptions.Topic);
        _eventPublisher = new CacheEventPublisher(cacheName, _topicProvider, cacheEventFactory, logger);
        var connectionMonitorEnabled = multiLayerCacheOptions.ConnectionMonitorEnabled ?? cacheOptions.ConnectionMonitorEnabled;
        _connectionState = connectionMonitorEnabled ? GetConnectionMonitor(innerCache, _topicProvider) : NullConnectionStateMonitor.Instance;
        _useLocalOnlyWhenDisconnected = (multiLayerCacheOptions.UseLocalOnlyWhenDisconnected ?? false) && connectionMonitorEnabled;
        _localLock = localLock;
        _distributedLock = distributedLock;
        _lockKeyStrategy = multiLayerCacheOptions.LockKeyStrategy ?? new DefaultDistributedLockKeyStrategy(cacheOptions.Separator);
        _localLockKeyPrefix = cacheName + cacheOptions.Separator;
        _distributedLockExpiry = multiLayerCacheOptions.DistributedLockExpiry ?? TimeSpan.FromSeconds(5);
        _distributedLockTimeout = multiLayerCacheOptions.DistributedLockTimeout ?? TimeSpan.FromMilliseconds(500);
        _localLockTimeout = multiLayerCacheOptions.LocalLockTimeout ?? TimeSpan.FromMilliseconds(500);
        _localLockEnabled = multiLayerCacheOptions.LocalLockEnabled ?? true;
        _distributedLockEnabled = multiLayerCacheOptions.DistributedLockEnabled ?? false;
        _policyFactory = policyFactory;
        Name = cacheName;
        _rehydrator = new RehydrationCoordinator(cacheName, _clock, distributedLock, _lockKeyStrategy, telemetryProvider, logger);
    }

    public string Name { get; }

    protected async ValueTask<TResult> RunUnderLocksAsync<TResult>(
        CacheKey cacheKey,
        Func<ValueTask<TResult>> readCachedAsync,
        Func<TResult, bool> isHit,
        Func<CancellationToken, ValueTask<TResult>> runGeneratorAndStoreAsync,
        CancellationToken token,
        LockProfile? policyLock = null)
    {
        var localLockEnabled = policyLock?.LocalLockEnabled ?? _localLockEnabled;
        var distributedLockEnabled = policyLock?.DistributedLockEnabled ?? _distributedLockEnabled;
        // Per-call LockProfile bypasses options validators; mirror LockSettingsValidator's accepted ranges and fall back when out-of-range.
        var localLockTimeout = PositiveOrFallback(policyLock?.LocalLockTimeout, _localLockTimeout);
        var distributedLockTimeout = NonNegativeOrFallback(policyLock?.DistributedLockTimeout, _distributedLockTimeout);
        var distributedLockExpiry = PositiveOrFallback(policyLock?.DistributedLockExpiry, _distributedLockExpiry);

        IDisposable? localLock = null;
        IAsyncDisposable? distributedLock = null;
        try
        {
            if (localLockEnabled)
            {
                localLock = await TryAcquireLocalLockAsync(cacheKey, localLockTimeout, token).ConfigureAwait(false);
                var fromCache = await readCachedAsync().ConfigureAwait(false);
                if (isHit(fromCache))
                {
                    return fromCache;
                }
            }

            if (distributedLockEnabled)
            {
                var lockKey = _lockKeyStrategy.GetLockKey(cacheKey);
                distributedLock = await _distributedLock.AcquireAsync(lockKey, distributedLockExpiry, distributedLockTimeout, token).ConfigureAwait(false);
                var fromCache = await readCachedAsync().ConfigureAwait(false);
                if (isHit(fromCache))
                {
                    return fromCache;
                }
            }

            return await runGeneratorAndStoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (distributedLock is not null)
                {
                    await distributedLock.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                localLock?.Dispose();
            }
        }
    }

    protected Task<TResult> InvokeFactoryAsync<TResult>(
        CacheKey cacheKey,
        Func<CancellationToken, Task<TResult>> factory,
        TimeSpan? factoryTimeout,
        CancellationToken token) =>
        FactoryTimeout.RunAsync(factory, factoryTimeout, cacheKey, Name, Telemetry, token);

    private static TimeSpan PositiveOrFallback(TimeSpan? value, TimeSpan fallback) =>
        value is { } v && v > TimeSpan.Zero ? v : fallback;

    private static TimeSpan NonNegativeOrFallback(TimeSpan? value, TimeSpan fallback) =>
        value is { } v && v >= TimeSpan.Zero ? v : fallback;

    protected static TimeSpan? ApplyJitter(TimeSpan? duration, TimeSpan? maxJitter, DateTimeOffset utcNow)
    {
        if (duration is not { } d || d <= TimeSpan.Zero || maxJitter is not { } max || max <= TimeSpan.Zero)
        {
            return duration;
        }
        var bonusTicks = Random.Shared.NextInt64(max.Ticks);
        // Saturate at TimeSpan.MaxValue then at remaining DateTime range. A small slack covers the gap
        // between this UtcNow and the slightly-later UtcNow inside CacheClock.ToDateTimeOffset.
        var sumTicks = d.Ticks > TimeSpan.MaxValue.Ticks - bonusTicks ? TimeSpan.MaxValue.Ticks : d.Ticks + bonusTicks;
        var maxFromNow = DateTime.MaxValue.Ticks - utcNow.UtcTicks - ClockDriftSlack.Ticks;
        return new TimeSpan(Math.Max(0, Math.Min(sumTicks, maxFromNow)));
    }

    /// <summary>
    /// Resolves the L2 write duration, applying jitter only when the caller did not pass an explicit
    /// expiration. Resolves the full fallback chain (<c>policy.DistributedExpiration</c> →
    /// <c>IMultilayerCacheOptions.DefaultExpiration</c>) BEFORE jittering so the options-default path
    /// is jittered too — not just the policy path.
    /// </summary>
    protected TimeSpan? ResolveWriteDuration(CachePolicy policy, TimeSpan? callerExpiration = null)
    {
        if (callerExpiration is not null)
        {
            return callerExpiration;
        }
        var resolved = policy.DistributedExpiration ?? _multiLayerCacheOptions.DefaultExpiration;
        return ApplyJitter(resolved, policy.JitterMaxDuration, _clock.UtcNow);
    }

    private async ValueTask<IDisposable?> TryAcquireLocalLockAsync(CacheKey cacheKey, TimeSpan localLockTimeout, CancellationToken token)
    {
        var lockKey = _localLockKeyPrefix + cacheKey.Name;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        // CancelAfter throws on > Int32.MaxValue ms; clamp so misconfigured Lock.LocalLockTimeout degrades gracefully.
        var clampedTimeout = localLockTimeout.TotalMilliseconds > int.MaxValue
            ? TimeSpan.FromMilliseconds(int.MaxValue)
            : localLockTimeout;
        linkedCts.CancelAfter(clampedTimeout);
        try
        {
            return await _localLock.AcquireAsync(lockKey, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    protected ICachingTelemetryProvider Telemetry { get; }

    protected bool GetInnerCacheDisconnected() => _useLocalOnlyWhenDisconnected && !_connectionState.IsConnected;

    private IConnectionState GetConnectionMonitor(params object[] connectionStates)
    {
        var lst = connectionStates.OfType<IConnectionState>().ToArray();
        return lst.Length == 0 ? NullConnectionStateMonitor.Instance : new ConnectionStateMonitor(Telemetry, _multiLayerCacheOptions.ConnectionMonitorPeriod ?? TimeSpan.FromSeconds(5), lst);
    }
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _monitor.Dispose();
                _memoryCache.Dispose();
                if (_connectionState is IDisposable connectionState)
                {
                    connectionState.Dispose();
                }
            }
            _disposed = true;
        }
    }

    private static void ValidateExpirationOptions(IMultilayerCacheOptions options)
    {
        if (options.LocalMaxExpiration.HasValue &&
            options.LocalMaxExpirationDisconnected.HasValue &&
            options.LocalMaxExpirationDisconnected.Value > options.LocalMaxExpiration.Value)
        {
            throw new ArgumentException(
                $"{nameof(options.LocalMaxExpirationDisconnected)} ({options.LocalMaxExpirationDisconnected.Value}) must be less than or equal to {nameof(options.LocalMaxExpiration)} ({options.LocalMaxExpiration.Value}).",
                nameof(options));
        }
    }

    private static void ValidateLockOptions(IMultilayerCacheOptions options)
    {
        if (options.DistributedLockExpiry is { } expiry && expiry <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                expiry,
                $"{nameof(IMultilayerCacheOptions.DistributedLockExpiry)} must be greater than zero.");
        }
        if (options.DistributedLockTimeout is { } timeout && timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                timeout,
                $"{nameof(IMultilayerCacheOptions.DistributedLockTimeout)} must be greater than or equal to zero.");
        }
        if (options.LocalLockTimeout is { } localTimeout && localTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                localTimeout,
                $"{nameof(IMultilayerCacheOptions.LocalLockTimeout)} must be greater than zero.");
        }
        if (options.LocalLockEnabled == false && options.DistributedLockEnabled == true)
        {
            throw new ArgumentException(
                $"{nameof(IMultilayerCacheOptions.LocalLockEnabled)}=false with " +
                $"{nameof(IMultilayerCacheOptions.DistributedLockEnabled)}=true weakens single-flight: " +
                "every local caller competes independently for the distributed lock, multiplying " +
                "LockTakeAsync round-trips per node. Enable both or disable both.",
                nameof(options));
        }
    }
}
