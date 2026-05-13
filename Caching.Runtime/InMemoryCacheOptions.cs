using UiPath.Platform.Caching.Locking;

namespace UiPath.Platform.Caching;

public class InMemoryCacheOptions : IMultilayerCacheOptions, IMemoryCacheOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan? DefaultExpiration { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);

    public ISystemClock? Clock { get; set; }

    public ICacheEntryFactory? EntryFactory { get; set; }

    public ICacheKeyStrategy? CacheKeyStrategy { get; set; }

    public bool TrackStatistics { get; set; } = true;

    public TimeSpan StatisticsFlushInterval { get; set; } = TimeSpan.FromMinutes(1);

    public bool BroadcastEnable { get; set; }

    public string? Topic { get; set; }

    public TimeSpan? PrimaryMaxExpiration { get; set; } = TimeSpan.FromHours(1);

    public ITopicKeyStrategy? TopicKeyStrategy { get; set; }

    public bool? ConnectionMonitorEnabled { get; set; }

    public TimeSpan? ConnectionMonitorPeriod { get; set; } = TimeSpan.FromSeconds(5);

    public long? SizeLimit { get; set; }

    public double? CompactionPercentage { get; set; }

    public ICacheEntrySizeProvider? SizeProvider { get; set; }

    public bool? UsePrimaryOnlyWhenDisconnected { get; set; }

    public TimeSpan? PrimaryMaxExpirationDisconnected { get; set; } = TimeSpan.FromSeconds(30);

    public bool? LocalLockEnabled { get; set; } = true;

    public TimeSpan? LocalLockTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The following four properties (<see cref="DistributedLockEnabled"/>,
    /// <see cref="DistributedLockTimeout"/>, <see cref="DistributedLockExpiry"/>,
    /// <see cref="LockKeyStrategy"/>) are inert for this provider's runtime behavior — the in-memory
    /// cache has no cross-node lock to acquire. They exist only to satisfy the
    /// <see cref="IMultilayerCacheOptions"/> contract; setting them does not change cache behavior.
    /// Note: shape and cross-property validation still applies at registration time, so
    /// out-of-range values (e.g. negative <see cref="DistributedLockTimeout"/>) and contradictory
    /// combinations (<see cref="LocalLockEnabled"/>=false with <see cref="DistributedLockEnabled"/>=true)
    /// will fail startup even though they would otherwise be no-ops here.
    /// </summary>
    public bool? DistributedLockEnabled { get; set; }

    public TimeSpan? DistributedLockTimeout { get; set; }

    public TimeSpan? DistributedLockExpiry { get; set; }

    public IDistributedLockKeyStrategy? LockKeyStrategy { get; set; }
}
