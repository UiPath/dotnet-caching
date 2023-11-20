#if !NET6_0
using System.Globalization;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

internal sealed class CacheMemoryMonitor : IDisposable
{
    private readonly string _statsMetricName;
    private readonly MemoryCache _memoryCache;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly PeriodicTimer _timer;
    private readonly CancellationToken _cancelationToken;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private bool _disposed;

    public CacheMemoryMonitor(string statsMetricName,
        TimeSpan statisticsFlushInterval,
        MemoryCache memoryCache,
        ICachingTelemetryProvider telemetryProvider)
    {
        _statsMetricName = statsMetricName;
        _memoryCache = memoryCache;
        _telemetryProvider = telemetryProvider;
        _timer = new PeriodicTimer(statisticsFlushInterval);
        _cancelationToken = _cancellationTokenSource.Token;
        MonitorTask = Task.Run(StartMonitor, _cancelationToken);
    }

    internal Task MonitorTask { get; }

    private async Task StartMonitor()
    {
        while (!(_disposed || _cancelationToken.IsCancellationRequested) && await _timer.WaitForNextTickAsync())
        {
            var currentStats = _memoryCache.GetCurrentStatistics();
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
        _disposed = true;
        _cancellationTokenSource.Cancel();
        _timer.Dispose();
    }
}
#endif
