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
    protected readonly bool _usePrimaryOnlyWhenDisconnected;
    private readonly ILocalLock _localLock;
    private readonly IDistributedLock _distributedLock;
    private readonly IDistributedLockKeyStrategy _lockKeyStrategy;
    private readonly string _localLockKeyPrefix;
    private readonly TimeSpan _distributedLockExpiry;
    private readonly TimeSpan _distributedLockTimeout;
    private readonly TimeSpan _localLockTimeout;
    private readonly bool _localLockEnabled;
    private readonly bool _distributedLockEnabled;

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
        _usePrimaryOnlyWhenDisconnected = (multiLayerCacheOptions.UsePrimaryOnlyWhenDisconnected ?? false) && connectionMonitorEnabled;
        _localLock = localLock;
        _distributedLock = distributedLock;
        _lockKeyStrategy = multiLayerCacheOptions.LockKeyStrategy ?? new DefaultDistributedLockKeyStrategy(cacheOptions.Separator);
        _localLockKeyPrefix = cacheName + cacheOptions.Separator;
        _distributedLockExpiry = multiLayerCacheOptions.DistributedLockExpiry ?? TimeSpan.FromSeconds(5);
        _distributedLockTimeout = multiLayerCacheOptions.DistributedLockTimeout ?? TimeSpan.FromMilliseconds(500);
        _localLockTimeout = multiLayerCacheOptions.LocalLockTimeout ?? TimeSpan.FromMilliseconds(500);
        _localLockEnabled = multiLayerCacheOptions.LocalLockEnabled ?? true;
        _distributedLockEnabled = multiLayerCacheOptions.DistributedLockEnabled ?? false;
        Name = cacheName;
    }

    public string Name { get; }

    protected async ValueTask<TResult> RunUnderLocksAsync<TResult>(
        CacheKey cacheKey,
        Func<ValueTask<TResult>> readCachedAsync,
        Func<TResult, bool> isHit,
        Func<CancellationToken, ValueTask<TResult>> runGeneratorAndStoreAsync,
        CancellationToken token)
    {
        IDisposable? localLock = null;
        IAsyncDisposable? distributedLock = null;
        try
        {
            if (_localLockEnabled)
            {
                localLock = await TryAcquireLocalLockAsync(cacheKey, token).ConfigureAwait(false);
                var fromCache = await readCachedAsync().ConfigureAwait(false);
                if (isHit(fromCache))
                {
                    return fromCache;
                }
            }

            if (_distributedLockEnabled)
            {
                var lockKey = _lockKeyStrategy.GetLockKey(cacheKey);
                distributedLock = await _distributedLock.AcquireAsync(lockKey, _distributedLockExpiry, _distributedLockTimeout, token).ConfigureAwait(false);
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

    private async ValueTask<IDisposable?> TryAcquireLocalLockAsync(CacheKey cacheKey, CancellationToken token)
    {
        var lockKey = _localLockKeyPrefix + cacheKey.Name;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        linkedCts.CancelAfter(_localLockTimeout);
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

    protected bool GetInnerCacheDisconnected() => _usePrimaryOnlyWhenDisconnected && !_connectionState.IsConnected;

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
        if (options.PrimaryMaxExpiration.HasValue &&
            options.PrimaryMaxExpirationDisconnected.HasValue &&
            options.PrimaryMaxExpirationDisconnected.Value > options.PrimaryMaxExpiration.Value)
        {
            throw new ArgumentException(
                $"{nameof(options.PrimaryMaxExpirationDisconnected)} ({options.PrimaryMaxExpirationDisconnected.Value}) must be less than or equal to {nameof(options.PrimaryMaxExpiration)} ({options.PrimaryMaxExpiration.Value}).",
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
