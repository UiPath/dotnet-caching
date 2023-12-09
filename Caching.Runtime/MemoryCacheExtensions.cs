using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public static class MemoryCacheExtensions
{
#pragma warning disable RCS1175, RCS1163 // Unused 'this' parameter. Unused parameter.
    public static IDisposable Monitor(this IMemoryCache cache, ICacheOptions cacheOptions, ICachingTelemetryProvider telemetryProvider, string metricName)
#pragma warning restore RCS1175 // Unused 'this' parameter.
    {
#if !NET6_0
        if (cacheOptions is IMemoryStatisticsOptions statsOptions && statsOptions.TrackStatistics && cache is MemoryCache memCache)
        {
            var metric = $"Caching.MemoryCache.{metricName}.MemoryCacheStatistics";
            return new CacheMemoryMonitor(metric, statsOptions.StatisticsFlushInterval, memCache, telemetryProvider);
        }
#endif
        return Disposable.Empty;
    }
}
