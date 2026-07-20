namespace UiPath.Caching;

/// <summary>
/// Options for the multilayer <see cref="ISetCache"/> (the <c>InMemoryRedis</c> provider): a local
/// in-process snapshot (L1) in front of the Redis set cache (L2). The Redis tier itself is
/// configured via <see cref="Redis.RedisSetCacheOptions"/> / <see cref="Redis.RedisCacheOptions"/>.
/// </summary>
public sealed class InMemoryRedisSetCacheOptions : IMemoryCacheOptions
{
    /// <summary>Indicates whether the multilayer set cache is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Upper bound on how long a locally-cached set snapshot is served before it is re-fetched from
    /// Redis. This also bounds the staleness window for mutations performed on other nodes, since
    /// this tier does not subscribe to cross-node broadcast invalidation. <see langword="null"/>
    /// caches snapshots without a time bound (not recommended for multi-node deployments).
    /// Defaults to one minute.
    /// </summary>
    public TimeSpan? LocalMaxExpiration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Monitors the Redis tier's connection state, mirroring <c>InMemoryRedisCacheOptions</c>. Must be
    /// enabled for <see cref="UseLocalOnlyWhenDisconnected"/> to take effect; when enabled without it,
    /// locally-cached snapshots are dropped instead of served while Redis is unreachable. Disabled by
    /// default.
    /// </summary>
    public bool ConnectionMonitorEnabled { get; set; }

    /// <summary>How often the connection monitor re-evaluates a failed connection. Defaults to five seconds.</summary>
    public TimeSpan? ConnectionMonitorPeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Serves reads from the local snapshot and applies mutations locally while Redis is unreachable,
    /// instead of failing through to the disconnected tier. Local state written while disconnected
    /// expires after <see cref="LocalMaxExpirationDisconnected"/>. Requires
    /// <see cref="ConnectionMonitorEnabled"/>. Disabled by default.
    /// </summary>
    public bool UseLocalOnlyWhenDisconnected { get; set; }

    /// <summary>
    /// Upper bound on the lifetime of local set state written while Redis is unreachable (see
    /// <see cref="UseLocalOnlyWhenDisconnected"/>), so it dies quickly once connectivity returns.
    /// Defaults to thirty seconds.
    /// </summary>
    public TimeSpan? LocalMaxExpirationDisconnected { get; set; } = TimeSpan.FromSeconds(30);

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
