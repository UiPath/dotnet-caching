using System.Globalization;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Hybrid;

internal sealed class CacheMemoryMonitor : IDisposable
{
    private readonly string _statsMetricName;

    private readonly TimeSpan _statisticsFlushInterval;

    private readonly MemoryCache _memoryCache;

    private readonly ICachingTelemetryProvider _telemetryProvider;

    [SuppressMessage("SonarLint.Rule", "S2933:Inner class members should not shadow outer class \"this\" or field names")]
#pragma warning disable IDE0052 // Remove unread private members
    private readonly Task? _monitorTask;
#pragma warning restore IDE0052 // Remove unread private members

    private PeriodicTimer? _timer;


    public CacheMemoryMonitor(string statsMetricName,
        TimeSpan statisticsFlushInterval,
        MemoryCache memoryCache,
        ICachingTelemetryProvider telemetryProvider)
    {
        _statsMetricName = statsMetricName;
        _statisticsFlushInterval = statisticsFlushInterval;
        _memoryCache = memoryCache;
        _telemetryProvider = telemetryProvider;
        _monitorTask = Task.Run(StartMonitor);
    }

    private async Task StartMonitor()
    {
        _timer = new PeriodicTimer(_statisticsFlushInterval);
        while (await _timer.WaitForNextTickAsync())
        {
            MemoryCacheStatistics? currentStats = _memoryCache.GetCurrentStatistics();
            if (currentStats == null)
            {
                continue;
            }

            _telemetryProvider.TrackMetric(_statsMetricName, currentStats.CurrentEntryCount, new Dictionary<string, string>
            {
                { "CurrentEntryCount", currentStats.CurrentEntryCount.ToString(CultureInfo.InvariantCulture) },
                { "CurrentEstimatedSize", currentStats.CurrentEstimatedSize.GetValueOrDefault().ToString(CultureInfo.InvariantCulture) },
                { "TotalHits", currentStats.TotalHits.ToString(CultureInfo.InvariantCulture) },
                { "TotalMisses", currentStats.TotalMisses.ToString(CultureInfo.InvariantCulture) },
            });
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
