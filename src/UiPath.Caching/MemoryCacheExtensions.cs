using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

[ExcludeFromCodeCoverage]
public static class MemoryCacheExtensions
{
#pragma warning disable RCS1175, RCS1163 // Unused 'this' parameter. Unused parameter.
    public static IDisposable Monitor(this IMemoryCache cache, ICacheOptions cacheOptions, ICachingTelemetryProvider telemetryProvider, string name)
#pragma warning restore RCS1175 // Unused 'this' parameter.
    {
        if (cacheOptions is IMemoryCacheOptions memoryOptions && memoryOptions.TrackStatistics && cache is MemoryCache memCache)
        {
            return new CacheMemoryMonitor(name, memoryOptions.StatisticsFlushInterval, memCache, telemetryProvider);
        }
        return Disposable.Empty;
    }
}
