using UiPath.Platform.Caching.Memory;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public static class MemoryCacheExtensions
{
    public static IDisposable Monitor(this IMemoryCache cache, ICacheOptions cacheOptions, ICachingTelemetryProvider telemetryProvider, string metricName)
    {
        if (cacheOptions is IMemoryStatisticsOptions statsOptions && statsOptions.TrackStatistics && cache is MemoryCache memCache)
        {
            var metric = $"Caching.MemoryCache.{metricName}.MemoryCacheStatistics";
            return new CacheMemoryMonitor(metric, statsOptions.StatisticsFlushInterval, memCache, telemetryProvider);
        }
        return Disposable.Empty;
    }
}
