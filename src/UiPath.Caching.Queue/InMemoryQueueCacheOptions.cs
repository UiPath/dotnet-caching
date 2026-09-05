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

    /// <summary>
    /// Cap on a stored set's lifetime. <see langword="null"/>, the default: with no backing tier the
    /// memory cache <em>is</em> the store, so <see cref="DefaultExpiration"/> already bounds every set
    /// and a second cap would only shorten it.
    /// </summary>
    public TimeSpan? LocalMaxExpiration { get; set; }

    /// <summary>
    /// Kept for parity with <see cref="InMemoryRedisQueueCacheOptions"/>. This provider has no backing
    /// tier whose connection could fail, so the monitor has nothing to observe; leave it off.
    /// </summary>
    public bool ConnectionMonitorEnabled { get; set; }

    /// <inheritdoc cref="ConnectionMonitorEnabled"/>
    public TimeSpan? ConnectionMonitorPeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc cref="ConnectionMonitorEnabled"/>
    public bool UseLocalOnlyWhenDisconnected { get; set; }

    /// <inheritdoc cref="ConnectionMonitorEnabled"/>
    public TimeSpan? LocalMaxExpirationDisconnected { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default whole-set lifetime applied when no explicit expiration or <see cref="CachePolicy"/>
    /// expiration is supplied. <see langword="null"/> means "inherit", which resolves to
    /// <see cref="CachePolicy.DefaultDistributedExpiration"/>; to keep a set forever, set
    /// <see cref="TimeSpan.MaxValue"/>. Every add re-applies the resolved expiration, matching
    /// <see cref="ISetCache"/>.
    /// </summary>
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
