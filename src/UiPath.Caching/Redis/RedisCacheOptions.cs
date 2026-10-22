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
    /// Read a value and its expiration with one read-only script (<c>EVAL_RO</c>) on Redis 7 and later, which allocates
    /// less than a MULTI/EXEC transaction. On by default. Before Redis 7, and on a server that cannot run the script,
    /// reads use the transaction: they switch to it for good, with a warning, if the first script fails for any reason,
    /// or later if the server refuses one as unknown or not permitted. Set to <see langword="false"/> to never send a script.
    /// </summary>
    public bool ReadScriptsEnabled { get; set; } = true;

    /// <summary>How many fields of one hash a single Redis command carries; a hash call over more is split into commands of this size, so none blocks the server for long.</summary>
    public int HashFieldBatchSize { get; set; } = 1000;

    /// <summary>Member-wise copy, so a caller can vary one setting without mutating the DI singleton.</summary>
    internal RedisCacheOptions ShallowCopy() => (RedisCacheOptions)MemberwiseClone();
}
