using UiPath.Platform.Caching.Locking;

namespace UiPath.Platform.Caching;

public interface IMultilayerCacheOptions : ICacheOptions
{
    public string? Topic { get; set; }

    public ITopicKeyStrategy? TopicKeyStrategy { get; set; }

    /// <summary>L1 (in-memory tier) cap on entry lifetime. Aligns with .NET HybridCache's `LocalCacheExpiration` naming.</summary>
    public TimeSpan? LocalMaxExpiration { get; set; }

    /// <summary>Renamed to <see cref="LocalMaxExpiration"/>. The old name still works; assignments forward to the new property.</summary>
    [Obsolete("Renamed to LocalMaxExpiration. The old name still works (assignments forward to the new property) but will be removed in a future release.")]
    public TimeSpan? PrimaryMaxExpiration { get; set; }

    public TimeSpan? ConnectionMonitorPeriod { get; set; }

    /// <summary>Serve from L1 only (without falling back to default) when the L2 connection is unhealthy. Aligns with Local/Distributed tier naming.</summary>
    public bool? UseLocalOnlyWhenDisconnected { get; set; }

    /// <summary>Renamed to <see cref="UseLocalOnlyWhenDisconnected"/>. The old name still works; assignments forward to the new property.</summary>
    [Obsolete("Renamed to UseLocalOnlyWhenDisconnected. The old name still works (assignments forward to the new property) but will be removed in a future release.")]
    public bool? UsePrimaryOnlyWhenDisconnected { get; set; }

    /// <summary>L1 cap on entry lifetime while the L2 connection is unhealthy (paired with <see cref="UseLocalOnlyWhenDisconnected"/>).</summary>
    public TimeSpan? LocalMaxExpirationDisconnected { get; set; }

    /// <summary>Renamed to <see cref="LocalMaxExpirationDisconnected"/>. The old name still works; assignments forward to the new property.</summary>
    [Obsolete("Renamed to LocalMaxExpirationDisconnected. The old name still works (assignments forward to the new property) but will be removed in a future release.")]
    public TimeSpan? PrimaryMaxExpirationDisconnected { get; set; }

    /// <summary>
    /// Enables the per-key in-process lock that serializes the cache-miss generator across
    /// local callers. Defaults to true.
    /// Note: disabling this while <see cref="DistributedLockEnabled"/> is on weakens single-flight
    /// more than it appears — every local caller competes independently for the distributed lock,
    /// so under concurrent load the same node can issue multiple <c>LockTakeAsync</c> round-trips
    /// for the same key, and the distributed lock's contention timeout (rather than the local
    /// lock) becomes the only bound on how many generators run.
    /// </summary>
    public bool? LocalLockEnabled { get; set; }

    /// <summary>
    /// How long a caller blocks trying to acquire the per-key in-process lock before giving up
    /// and running the generator anyway. Defaults to 500 ms (matches <see cref="DistributedLockTimeout"/>).
    /// Mirrors the distributed lock's fail-open posture: on timeout the caller proceeds without
    /// the local lock, trading single-flight for liveness when a generator stalls. Pick a value
    /// above your p99 generator runtime plus <see cref="DistributedLockTimeout"/> if distributed
    /// locking is also enabled.
    /// </summary>
    public TimeSpan? LocalLockTimeout { get; set; }

    /// <summary>
    /// Enables the distributed (cross-node) lock around the cache-miss generator. Has no effect
    /// on cache providers that don't supply a real <see cref="IDistributedLock"/> implementation
    /// (e.g. the in-memory-only provider, which always passes <see cref="NullDistributedLock"/>).
    /// </summary>
    public bool? DistributedLockEnabled { get; set; }

    /// <summary>
    /// How long a waiter blocks trying to acquire the distributed lock before giving up and
    /// running the generator anyway. Defaults to 500 ms. Contention longer than this value
    /// re-stampedes the generator across nodes — pick a value that comfortably exceeds your
    /// generator's typical runtime.
    /// </summary>
    public TimeSpan? DistributedLockTimeout { get; set; }

    /// <summary>
    /// TTL for the Redis lock. Acts as a safety net so a crashed holder doesn't deadlock the
    /// key forever. Defaults to 5 s — biased toward keeping the lock held for the duration of
    /// a typical slow upstream (DB / HTTP) generator rather than minimizing post-crash contention.
    /// If the generator runs longer than this value the lock auto-expires and subsequent waiters
    /// acquire it, which can produce duplicate generator invocations under load. Set above your
    /// p99 generator runtime, or accept the partial herd as a trade-off.
    /// </summary>
    public TimeSpan? DistributedLockExpiry { get; set; }

    /// <summary>
    /// Strategy that derives the Redis distributed-lock key from a cache key. The default
    /// strategy appends <c>":lck"</c> to <see cref="CacheKey.Name"/>.
    /// Note: the default does NOT apply your inner-cache prefix scheme (see
    /// <see cref="ICacheKeyStrategy"/>). Two applications sharing a Redis with the same
    /// <see cref="CacheKey.Name"/> have isolated values but COLLIDING lock keys. If you use
    /// a non-trivial cache-key strategy (e.g. <see cref="PrefixCacheKeyStrategy"/>), supply
    /// a matching lock-key strategy here too.
    /// </summary>
    public IDistributedLockKeyStrategy? LockKeyStrategy { get; set; }
}
