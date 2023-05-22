namespace UiPath.Platform.Caching.Hybrid;

public class HybridCacheOptions : CacheOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    public bool TrackStatistics { get; set; }

    public TimeSpan StatisticsFlushInterval { get; set; } = TimeSpan.FromMinutes(5);

    public BroadcastOptions? Broadcast { get; set; }
}
