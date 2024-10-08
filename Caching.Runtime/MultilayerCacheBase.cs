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
    protected readonly IConnectionState _connectionEventSource;
    protected readonly ITopicProvider _topicProvider;
    protected readonly ICachingTelemetryProvider _telemetryProvider;

    protected MultilayerCacheBase(
        string cacheName,
        object innerCache,
        Func<IMemoryCache> memoryCacheAccessor,
        ITopicFactory topicFactory,
        ICacheEventFactory cacheEventFactory,
        ICachingTelemetryProvider telemetryProvider,
        IMultilayerCacheOptions multiLayerCacheOptions,
        CacheOptions cacheOptions,
        ILogger logger)
    {
        _logger = logger;
        _multiLayerCacheOptions = multiLayerCacheOptions;
        _memoryCache = memoryCacheAccessor();
        _telemetryProvider = telemetryProvider;
        _cacheEntryFactory = _multiLayerCacheOptions.EntryFactory ?? new CacheEntryFactory();
        _monitor = _memoryCache.Monitor(multiLayerCacheOptions, _telemetryProvider, GetType().Name);
        _clock = new CacheClock(_multiLayerCacheOptions.Clock, _multiLayerCacheOptions.DefaultExpiration);
        _topicProvider = topicFactory.Get(_multiLayerCacheOptions.Topic);
        _eventPublisher = new CacheEventPublisher(cacheName, _topicProvider, cacheEventFactory, logger);
        var connectionMonitorEnabled = multiLayerCacheOptions.ConnectionMonitorEnabled ?? cacheOptions.ConnectionMonitorEnabled;
        _connectionEventSource = connectionMonitorEnabled ? GetConnectionMonitor(innerCache, _topicProvider) : NullConnectionStateMonitor.Instance;
        Name = cacheName;
    }

    public string Name { get; }

    private IConnectionState GetConnectionMonitor(params object[] connectionStates)
    {
        var lst = connectionStates.OfType<IConnectionState>().ToArray();
        return lst.Length == 0 ? NullConnectionStateMonitor.Instance : new ConnectionStateMonitor(_telemetryProvider, lst);
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
                _connectionEventSource.Dispose();
            }
            _disposed = true;
        }
    }
}
