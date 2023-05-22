using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Memory;

public abstract class MemCacheBase : CacheBase, IDisposable
{
    private bool _disposed;
    private readonly IDisposable? _monitor;

    protected MemCacheBase(
        Func<MemCacheOptions, IMemoryCache> memoryCacheAccessor,
        IChangeTokenFactory changeTokenFactory,
        ICachingTelemetryProvider telemetryProvider,
        IOptions<MemCacheOptions> optionsAccessor)
        : base(optionsAccessor.Value)
    {
        CacheOptions = optionsAccessor.Value;
        MemoryCache = memoryCacheAccessor(CacheOptions);
        ChangeTokenFactory = changeTokenFactory;
        TelemetryProvider = telemetryProvider;
        if (CacheOptions.TrackStatistics && MemoryCache is MemoryCache memCache)
        {
            var statsMetricName = "Caching.MemoryCache." + GetType().Name + ".MemoryCacheStatistics";
            _monitor = new CacheMemoryMonitor(statsMetricName, CacheOptions.StatisticsFlushInterval, memCache, TelemetryProvider);
        }
    }

    protected IChangeTokenFactory ChangeTokenFactory { get; private set; }

    protected IMemoryCache MemoryCache { get; private set; }

    protected ICachingTelemetryProvider TelemetryProvider { get; private set; }

    protected MemCacheOptions CacheOptions { get; private set; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _monitor?.Dispose();
            MemoryCache?.Dispose();
        }

        _disposed = true;
    }
}
