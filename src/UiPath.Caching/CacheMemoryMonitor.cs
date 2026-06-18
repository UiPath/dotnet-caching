using System.Globalization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal sealed class CacheMemoryMonitor : IDisposable
{
    private readonly string _name;
    private readonly MemoryCache _memoryCache;
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly PeriodicTimer _timer;
    private readonly CancellationToken _cancelationToken;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private bool _disposed;

    public CacheMemoryMonitor(string name,
        TimeSpan statisticsFlushInterval,
        MemoryCache memoryCache,
        ICachingTelemetryProvider telemetryProvider)
    {
        _name = name;
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

            _telemetryProvider.TrackMetric(_name, currentStats.CurrentEntryCount,
            [
                new("CurrentEntryCount", currentStats.CurrentEntryCount.ToString(CultureInfo.InvariantCulture)),
                new("CurrentEstimatedSize", currentStats.CurrentEstimatedSize.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)),
                new("TotalHits", currentStats.TotalHits.ToString(CultureInfo.InvariantCulture)),
                new("TotalMisses", currentStats.TotalMisses.ToString(CultureInfo.InvariantCulture)),
                new("name", _name),
            ]);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _timer.Dispose();
    }
}
