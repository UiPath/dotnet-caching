using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Hybrid;

public abstract class HybridCacheBase : CacheBase, IDisposable
{
    private bool _disposed;
    private readonly IDisposable? _monitor;

    protected HybridCacheBase(
        Func<HybridCacheOptions, IMemoryCache> memoryCacheAccessor,
        IChangeTokenFactory changeTokenFactory,
        IChannelPublisher<ICacheEvent> channelPublisher,
        IChannelResolver channelResolver,
        ICacheEventFactory clearCacheEventFactory,
        ICachingTelemetryProvider telemetryProvider,
        IOptions<HybridCacheOptions> optionsAccessor)
        : base(optionsAccessor.Value)
    {
        CacheOptions = optionsAccessor.Value;
        MemoryCache = memoryCacheAccessor(CacheOptions);
        ChangeTokenFactory = changeTokenFactory;
        ChannelPublisher = channelPublisher;
        ChannelResolver = channelResolver;
        ClearCacheEventFactory = clearCacheEventFactory;
        TelemetryProvider = telemetryProvider;
        if (CacheOptions.TrackStatistics && MemoryCache is MemoryCache memCache)
        {
            var statsMetricName = "Caching.MemoryCache." + GetType().Name + ".MemoryCacheStatistics";
            _monitor = new CacheMemoryMonitor(statsMetricName, CacheOptions.StatisticsFlushInterval, memCache, TelemetryProvider);
        }
    }

    protected IChangeTokenFactory ChangeTokenFactory { get; private set; }

    protected IChannelPublisher<ICacheEvent> ChannelPublisher { get; private set; }

    protected IMemoryCache MemoryCache { get; private set; }

    protected IChannelResolver ChannelResolver { get; private set; }

    protected ICacheEventFactory ClearCacheEventFactory { get; private set; }

    protected ICachingTelemetryProvider TelemetryProvider { get; private set; }

    protected HybridCacheOptions CacheOptions { get; private set; }

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

    protected virtual ICacheEvent CreateEvent(CacheEventData eventData) =>
        ClearCacheEventFactory.Create(KnownEventTypes.ClearCache, eventData);
}
