using UiPath.Caching.Locking;

namespace UiPath.Caching;

#pragma warning disable S1133 // Backward-compatible aliases are intentionally obsolete during the rename window.

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

    public TimeSpan? LocalMaxExpiration { get; set; }

    [Obsolete("Renamed to LocalMaxExpiration. The old name still works (assignments forward to the new property) but will be removed in a future release.")]
    public TimeSpan? PrimaryMaxExpiration
    {
        get => LocalMaxExpiration;
        set => LocalMaxExpiration = value;
    }

    public bool? ConnectionMonitorEnabled { get; set; }

    /// <summary>
    /// Persists generator-returned nulls and empty hashes as sentinels. <c>AddInMemoryRedis</c> propagates
    /// this flag to the inner <c>RedisCacheOptions.CacheNullValues</c>; custom <c>MultilayerCache</c>-over-
    /// <c>RedisCache</c> compositions must keep both options in sync themselves.
    /// </summary>
    public bool CacheNullValues { get; set; }

    public TimeSpan? ConnectionMonitorPeriod { get; set; } = TimeSpan.FromSeconds(5);

    public long? SizeLimit { get; set; }

    public double? CompactionPercentage { get; set; }

    public ICacheEntrySizeProvider? SizeProvider { get; set; }

    public bool? UseLocalOnlyWhenDisconnected { get; set; }

    [Obsolete("Renamed to UseLocalOnlyWhenDisconnected. The old name still works (assignments forward to the new property) but will be removed in a future release.")]
    public bool? UsePrimaryOnlyWhenDisconnected
    {
        get => UseLocalOnlyWhenDisconnected;
        set => UseLocalOnlyWhenDisconnected = value;
    }

    public TimeSpan? LocalMaxExpirationDisconnected { get; set; } = TimeSpan.FromSeconds(30);

    [Obsolete("Renamed to LocalMaxExpirationDisconnected. The old name still works (assignments forward to the new property) but will be removed in a future release.")]
    public TimeSpan? PrimaryMaxExpirationDisconnected
    {
        get => LocalMaxExpirationDisconnected;
        set => LocalMaxExpirationDisconnected = value;
    }

    public bool? LocalLockEnabled { get; set; } = true;

    public TimeSpan? LocalLockTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    public bool? DistributedLockEnabled { get; set; }

    public TimeSpan? DistributedLockTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan? DistributedLockExpiry { get; set; } = TimeSpan.FromSeconds(5);

    public IDistributedLockKeyStrategy? LockKeyStrategy { get; set; }
}

#pragma warning restore S1133
