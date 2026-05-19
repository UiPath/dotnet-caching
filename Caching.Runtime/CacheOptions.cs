namespace UiPath.Platform.Caching;
public class CacheOptions
{
    public const char KeySeparator = ':';

    public static readonly Uri MachineUri = new($"urn:{Environment.MachineName}".ToLowerInvariant());

    public bool Enabled { get; set; } = true;

    public bool TelemetryEnabled { get; set; } = true;

    public bool BroadcastEnabled { get; set; } = true;

    public bool ShardKeyEnabled { get; set; }

    public bool AuditEnabled { get; set; } = true;

    public string DefaultCache { get; set; } = KnownCacheProviderNames.InMemoryRedis;

    public string DefaultTopic { get; set; } = KnownTopicNames.RedisStreams;

    public Uri? SourceUri { get; set; } = MachineUri;

    public char Separator { get; set; } = KeySeparator;

    public string AppShortName { get; set; } = default!;

    public Type? CacheFactory { get; set; }

    public Type? CacheKeyStrategyFactory { get; set; }

    public Type? TopicKeyStrategyFactory { get; set; }

    public int LargeValueThreshold { get; set; } = 20_000;

    public bool ConnectionMonitorEnabled { get; set; }

    /// <summary>
    /// Size of the reusable semaphore pool inside the default local lock implementation.
    /// This is an allocation hint, not a hard concurrency cap — when the pool is exhausted the
    /// underlying <c>AsyncKeyedLocker</c> allocates fresh semaphores, so increasing this value
    /// only reduces GC pressure under high cold-miss concurrency, it does not throttle callers.
    /// Defaults to 100.
    /// </summary>
    public int LocalLockPoolSize { get; set; } = 100;

    /// <summary>
    /// Number of semaphores pre-allocated at startup for the default local lock pool.
    /// Must be in [0, <see cref="LocalLockPoolSize"/>]. Defaults to 10.
    /// </summary>
    public int LocalLockPoolInitialFill { get; set; } = 10;

    /// <summary>
    /// Initial wait between distributed-lock acquire retries when the lock is contended.
    /// The interval doubles after every failed attempt up to <see cref="DistributedLockMaxPollInterval"/>.
    /// Actual delays vary ±20% due to jitter applied at each step to de-synchronize concurrent waiters.
    /// Defaults to 50 ms. Must be greater than zero and ≤ <see cref="IMultilayerCacheOptions.DistributedLockTimeout"/>;
    /// otherwise the first retry sleep clamps to the remaining wait budget, neutralizing exponential backoff
    /// and limiting the loop to ~1–2 acquire attempts.
    /// </summary>
    public TimeSpan DistributedLockPollInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Upper bound for the exponential-backoff retry interval used by the distributed lock.
    /// Defaults to 500 ms. Must be greater than or equal to <see cref="DistributedLockPollInterval"/>.
    /// Values above <see cref="IMultilayerCacheOptions.DistributedLockTimeout"/> are accepted but have no
    /// effect — the remaining-wait clamp on each sleep takes over before the cap is reached.
    /// </summary>
    public TimeSpan DistributedLockMaxPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Per-cache-instance policies keyed by name. <see cref="ICache{T}"/> / <see cref="IHashCache{T}"/>
    /// bind a policy by name at construction (defaulting to <c>typeof(T).FullName!</c>); the resolved
    /// policy drives lock settings, foreground factory timeout, hydrating-cache behavior, and the
    /// L1/L2 TTLs via <see cref="CachePolicy.LocalExpiration"/> /
    /// <see cref="CachePolicy.LocalExpirationDisconnected"/> /
    /// <see cref="CachePolicy.DistributedExpiration"/>. Per-call <c>expiration</c> arguments still
    /// take precedence over the policy's L2 TTL. Names not registered fall back to
    /// <see cref="DefaultCachePolicy"/>.
    /// </summary>
    public IDictionary<string, CachePolicy> Policies { get; set; } = new Dictionary<string, CachePolicy>();

    /// <summary>
    /// Fallback policy used when a cache instance's name is not registered in <see cref="Policies"/>.
    /// When null, <c>ICachePolicyFactory.Default</c> is <c>CachePolicy.Empty</c> — provider-specific
    /// defaults (<c>IMultilayerCacheOptions.DefaultExpiration</c>, lock fields, etc.) are applied at
    /// call time by each impl, not propagated through the factory.
    /// </summary>
    public CachePolicy? DefaultCachePolicy { get; set; }
}
