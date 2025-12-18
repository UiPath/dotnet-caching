namespace UiPath.Platform.Caching;

public class InMemoryRedisCacheOptions : IMultilayerCacheOptions, IMemoryCacheOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan? DefaultExpiration { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);

    public ISystemClock? Clock { get; set; }

    public ICacheEntryFactory? EntryFactory { get; set; }

    public ICacheKeyStrategy? CacheKeyStrategy { get; set; }

    public bool TrackStatistics { get; set; } = true;

    public TimeSpan StatisticsFlushInterval { get; set; } = TimeSpan.FromMinutes(1);

    public string? Topic { get; set; }

    public ITopicKeyStrategy? TopicKeyStrategy { get; set; }

    public TimeSpan? PrimaryMaxExpiration { get; set; }

    public bool? ConnectionMonitorEnabled { get; set; }

    public TimeSpan? ConnectionMonitorPeriod { get; set; } = TimeSpan.FromSeconds(5);

    public long? SizeLimit { get; set; }

    public double? CompactionPercentage { get; set; }

    public ICacheEntrySizeProvider? SizeProvider { get; set; }

    public bool? UsePrimaryOnlyWhenDisconnected { get; set; }

    public TimeSpan? PrimaryMaxExpirationDisconnected { get; set; } = TimeSpan.FromSeconds(30);
}
