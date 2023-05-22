namespace UiPath.Platform.Caching.Memory;

public class MemCacheOptions : CacheOptions
{
    public string? InstanceName { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    public bool TrackStatistics { get; set; }

    public TimeSpan StatisticsFlushInterval { get; set; } = TimeSpan.FromMinutes(5);

    public bool EnableChangeToken { get; set; }
}
