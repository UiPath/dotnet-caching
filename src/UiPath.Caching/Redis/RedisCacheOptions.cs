namespace UiPath.Caching.Redis;

public class RedisCacheOptions : ICacheOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan? DefaultExpiration { get; set; } = CachePolicy.DefaultDistributedExpiration;

    public string KeyPrefix { get; set; } = string.Empty;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);

    public ICacheEntryFactory? EntryFactory { get; set; }

    public ICacheKeyStrategy? CacheKeyStrategy { get; set; }

    public IRedisKeyStrategyFactory? RedisKeyStrategyFactory { get; set; }

    public bool? ConnectionMonitorEnabled { get; set; }

    public bool CacheNullValues { get; set; }

    public bool KeyReadTelemetryEnabled { get; set; }

    /// <summary>
    /// Wait for the server to apply a refresh rather than sending it fire-and-forget. Off by default, keeping
    /// the round trip off the sliding-expiration path. Fire-and-forget makes a refresh unverifiable:
    /// <c>RefreshAsync</c> returns <see langword="false"/> either way, a rejection is neither logged nor
    /// retried, and the new deadline is not yet in effect when the call returns.
    /// </summary>
    public bool AwaitRefresh { get; set; }

    /// <summary>
    /// Which node answers a read. <see cref="RedisReadFrom.Master"/> by default: the master cannot return a key it has already removed,
    /// as a lagging replica can right after a write or an invalidation, and it answers faster. A replica answers only while no master is reachable.
    /// </summary>
    public RedisReadFrom ReadFrom { get; set; } = RedisReadFrom.Master;

    /// <summary>Member-wise copy, so a caller can vary one setting without mutating the DI singleton.</summary>
    internal RedisCacheOptions ShallowCopy() => (RedisCacheOptions)MemberwiseClone();
}
