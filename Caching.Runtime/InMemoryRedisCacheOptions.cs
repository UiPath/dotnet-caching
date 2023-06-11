using UiPath.Platform.Caching.Memory;

namespace UiPath.Platform.Caching;

public class InMemoryRedisCacheOptions : CacheOptionsBase, IMultilayerCacheOptions, IMemoryStatisticsOptions
{
    public bool TrackStatistics { get; set; }

    public TimeSpan StatisticsFlushInterval { get; set; } = TimeSpan.FromMinutes(5);

    public string? Topic { get; set; }

    public TimeSpan? PrimaryMaxExpiration { get; set; }
}
