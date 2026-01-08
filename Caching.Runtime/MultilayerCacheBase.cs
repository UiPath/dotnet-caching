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
        ILogger logger)
    {
        _logger = logger;
        _multiLayerCacheOptions = multiLayerCacheOptions;
        ValidateExpirationOptions(multiLayerCacheOptions);
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
        Name = cacheName;
    }

    public string Name { get; }

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
}
