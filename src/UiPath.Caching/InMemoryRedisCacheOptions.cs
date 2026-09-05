using UiPath.Caching.Locking;

namespace UiPath.Caching;

public class InMemoryRedisCacheOptions : IMultilayerCacheOptions, IMemoryCacheOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan? DefaultExpiration { get; set; } = CachePolicy.DefaultDistributedExpiration;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);

    public ICacheEntryFactory? EntryFactory { get; set; }

    public ICacheKeyStrategy? CacheKeyStrategy { get; set; }

    public bool TrackStatistics { get; set; } = true;

    public TimeSpan StatisticsFlushInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Publishes and consumes cross-node L1 invalidations for this provider. Set to <c>false</c> to run
    /// L1+L2 with no broadcast traffic, which is what you want on a single node or against a Redis-compatible
    /// store whose Streams support does not cover <c>XREADGROUP</c>. Defaults to <c>true</c>; note this is the
    /// opposite of <see cref="InMemoryCacheOptions.BroadcastEnable"/>, which is opt-in.
    /// <para>
    /// This flag can only narrow the app-wide setting, never widen it. The effective behavior is
    /// <see cref="CacheOptions.BroadcastEnabled"/> AND this property, so when broadcast is off app-wide
    /// setting this to <c>true</c> has no effect.
    /// </para>
    /// </summary>
    public bool BroadcastEnable { get; set; } = true;

    public string? Topic { get; set; }

    public ITopicKeyStrategy? TopicKeyStrategy { get; set; }

    public TimeSpan? LocalMaxExpiration { get; set; }

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

    public TimeSpan? LocalMaxExpirationDisconnected { get; set; } = TimeSpan.FromSeconds(30);

    public bool? LocalLockEnabled { get; set; } = true;

    public TimeSpan? LocalLockTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    public bool? DistributedLockEnabled { get; set; }

    public TimeSpan? DistributedLockTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan? DistributedLockExpiry { get; set; } = TimeSpan.FromSeconds(5);

    public IDistributedLockKeyStrategy? LockKeyStrategy { get; set; }

    /// <summary>Member-wise copy, so a caller can vary one setting without mutating the DI singleton.</summary>
    internal InMemoryRedisCacheOptions ShallowCopy() => (InMemoryRedisCacheOptions)MemberwiseClone();
}

