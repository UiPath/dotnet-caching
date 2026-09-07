namespace UiPath.Caching;

/// <summary>
/// Options for the in-memory queue-cache backing (the <c>InMemory</c> provider), shared by all its
/// collection kinds — like <see cref="InMemoryCacheOptions"/> is shared by the normal and hash
/// caches. Mirrors that type's memory-tier knobs; the set cache stores each set as a single
/// <see cref="IMemoryCache"/> entry.
/// </summary>
public sealed class InMemoryQueueCacheOptions : IMultilayerSetCacheOptions
{
    /// <summary>Indicates whether the in-memory set cache is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><see langword="null"/> by default: with no backing tier, <see cref="DefaultExpiration"/> already bounds every set.</summary>
    public TimeSpan? LocalMaxExpiration { get; set; }

    /// <summary>Parity with <see cref="InMemoryRedisQueueCacheOptions"/>; there is no backing tier to monitor, so leave it off.</summary>
    public bool ConnectionMonitorEnabled { get; set; }

    /// <inheritdoc cref="ConnectionMonitorEnabled"/>
    public TimeSpan? ConnectionMonitorPeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc cref="ConnectionMonitorEnabled"/>
    public bool UseLocalOnlyWhenDisconnected { get; set; }

    /// <inheritdoc cref="ConnectionMonitorEnabled"/>
    public TimeSpan? LocalMaxExpirationDisconnected { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whole-set lifetime when neither the call nor the policy names one. <see langword="null"/> resolves to <see cref="CachePolicy.DefaultDistributedExpiration"/>; <see cref="TimeSpan.MaxValue"/> keeps the set forever.</summary>
    public TimeSpan? DefaultExpiration { get; set; } = CachePolicy.DefaultDistributedExpiration;

    /// <inheritdoc/>
    public bool TrackStatistics { get; set; } = true;

    /// <inheritdoc/>
    public TimeSpan StatisticsFlushInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <inheritdoc/>
    public long? SizeLimit { get; set; }

    /// <inheritdoc/>
    public double? CompactionPercentage { get; set; }

    /// <inheritdoc/>
    public ICacheEntrySizeProvider? SizeProvider { get; set; }
}
