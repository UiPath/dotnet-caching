using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching;

[ExcludeFromCodeCoverage]
public static class MemoryCacheExtensions
{
#pragma warning disable RCS1175, RCS1163 // Unused 'this' parameter. Unused parameter.
    public static IDisposable Monitor(this IMemoryCache cache, ICacheOptions cacheOptions, ICachingTelemetryProvider telemetryProvider, string name)
#pragma warning restore RCS1175 // Unused 'this' parameter.
    {
        if (cacheOptions is IMemoryStatisticsOptions statsOptions && statsOptions.TrackStatistics && cache is MemoryCache memCache)
        {
            return new CacheMemoryMonitor(name, statsOptions.StatisticsFlushInterval, memCache, telemetryProvider);
        }
        return Disposable.Empty;
    }
}
