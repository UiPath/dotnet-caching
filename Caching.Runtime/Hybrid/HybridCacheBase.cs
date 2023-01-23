using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using UiPath.Platform.Caching.Broadcast;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Hybrid;

public abstract class HybridCacheBase : CacheBase, IDisposable
{
    private readonly string StatsMetricName;
    private PeriodicTimer? _timer;
    private bool _disposed;
#pragma warning disable IDE0052 // Remove unread private members
    private readonly Task _monitorMemoryCacheTask;
#pragma warning restore IDE0052 // Remove unread private members

    protected HybridCacheBase(
        Func<HybridCacheOptions, IMemoryCache> memoryCacheAccessor,
        IChangeTokenFactory changeTokenFactory,
        IChannelPublisher channelPublisher,
        IChannelResolver channelResolver,
        IClearCacheEventFactory clearCacheEventFactory,
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
        StatsMetricName = "Caching.MemoryCache." + GetType().Name + ".MemoryCacheStatistics";
        _monitorMemoryCacheTask = Task.Run(MonitorMemoryCache);

    }

    protected IChangeTokenFactory ChangeTokenFactory { get; private set; }

    protected IChannelPublisher ChannelPublisher { get; private set; }

    protected IMemoryCache MemoryCache { get; private set; }

    protected IChannelResolver ChannelResolver { get; private set; }

    protected IClearCacheEventFactory ClearCacheEventFactory { get; private set; }

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
            _timer?.Dispose();
            MemoryCache?.Dispose();
        }

        _disposed = true;
    }

    protected virtual IClearCacheEvent CreateEvent(ClearCacheEventData eventData) =>
        ClearCacheEventFactory.Create(eventData, CacheOptions.SourceUri);

    private async Task MonitorMemoryCache()
    {
        if (!CacheOptions.TrackStatistics)
        {
            return;
        }

        _timer = new PeriodicTimer(CacheOptions.StatisticsFlushInterval);
        while (!_disposed && await _timer.WaitForNextTickAsync())
        {
            if (MemoryCache is MemoryCache mc)
            {
                MemoryCacheStatistics? currentStats = mc.GetCurrentStatistics();
                if (currentStats != null)
                {

                    TelemetryProvider.TrackMetric(StatsMetricName, currentStats.CurrentEntryCount, new Dictionary<string, string>
                    {
                        { "CurrentEntryCount", currentStats.CurrentEntryCount.ToString(CultureInfo.InvariantCulture) },
                        { "CurrentEstimatedSize", currentStats.CurrentEstimatedSize.GetValueOrDefault().ToString(CultureInfo.InvariantCulture) },
                        { "TotalHits", currentStats.TotalHits.ToString(CultureInfo.InvariantCulture) },
                        { "TotalMisses", currentStats.TotalMisses.ToString(CultureInfo.InvariantCulture) },
                    });
                }
            }
            else
            {
                _timer.Dispose();
            }
        }
    }
}
