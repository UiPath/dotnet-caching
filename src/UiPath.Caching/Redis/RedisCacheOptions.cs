namespace UiPath.Caching.Redis;

public class RedisCacheOptions : ICacheOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan? DefaultExpiration { get; set; } = TimeSpan.FromHours(1);

    public string KeyPrefix { get; set; } = string.Empty;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);

    public ISystemClock? Clock { get; set; }

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

    /// <summary>Member-wise copy, so a caller can vary one setting without mutating the DI singleton.</summary>
    internal RedisCacheOptions ShallowCopy() => (RedisCacheOptions)MemberwiseClone();
}
