using UiPath.Caching.Locking;

namespace UiPath.Caching;

public interface IMultilayerCacheOptions : ICacheOptions
{
    string? Topic { get; set; }

    ITopicKeyStrategy? TopicKeyStrategy { get; set; }

    /// <summary>L1 (in-memory tier) cap on entry lifetime. Aligns with .NET HybridCache's `LocalCacheExpiration` naming.</summary>
    TimeSpan? LocalMaxExpiration { get; set; }

    TimeSpan? ConnectionMonitorPeriod { get; set; }

    /// <summary>Serve from L1 only (without falling back to default) when the L2 connection is unhealthy. Defaults to true; needs the connection monitor.</summary>
    bool? UseLocalOnlyWhenDisconnected { get; set; }

    /// <summary>Clear L1 when a Redis Pub/Sub broadcast reconnects, since invalidations sent while it was down never arrived. Defaults to true; needs the connection monitor.</summary>
    bool? ClearLocalOnReconnect { get; set; }

    /// <summary>L1 cap on entry lifetime while the L2 connection is unhealthy (paired with <see cref="UseLocalOnlyWhenDisconnected"/>).</summary>
    TimeSpan? LocalMaxExpirationDisconnected { get; set; }

    [Obsolete("Use LocalSingleFlightEnabled; the in-process lock it named is gone.")]
    bool? LocalLockEnabled { get => LocalSingleFlightEnabled; set => LocalSingleFlightEnabled = value; }

    [Obsolete("Use LocalSingleFlightTimeout; the in-process lock it named is gone.")]
    TimeSpan? LocalLockTimeout { get => LocalSingleFlightTimeout; set => LocalSingleFlightTimeout = value; }

    /// <summary>
    /// Callers of one process share one generator run per key: the first runs it and the rest join
    /// it. Defaults to true. Turning it off with <see cref="DistributedLockEnabled"/> on makes every
    /// local caller take the distributed lock for itself.
    /// </summary>
    bool? LocalSingleFlightEnabled { get; set; }

    /// <summary>
    /// How long <c>TryAddAsync</c> waits for a run that holds its key before it is told it lost;
    /// <c>GetOrAddAsync</c> callers join a run without waiting on it. Defaults to 500 ms.
    /// </summary>
    TimeSpan? LocalSingleFlightTimeout { get; set; }

    /// <summary>
    /// Enables the distributed (cross-node) lock around the cache-miss generator. Has no effect
    /// on cache providers that don't supply a real <see cref="IDistributedLock"/> implementation
    /// (e.g. the in-memory-only provider, which always passes <see cref="NullDistributedLock"/>).
    /// </summary>
    bool? DistributedLockEnabled { get; set; }

    /// <summary>
    /// How long a waiter blocks trying to acquire the distributed lock before giving up and
    /// running the generator anyway. Defaults to 500 ms. Contention longer than this value
    /// re-stampedes the generator across nodes — pick a value that comfortably exceeds your
    /// generator's typical runtime.
    /// </summary>
    TimeSpan? DistributedLockTimeout { get; set; }

    /// <summary>
    /// TTL for the Redis lock. Acts as a safety net so a crashed holder doesn't deadlock the
    /// key forever. Defaults to 5 s — biased toward keeping the lock held for the duration of
    /// a typical slow upstream (DB / HTTP) generator rather than minimizing post-crash contention.
    /// If the generator runs longer than this value the lock auto-expires and subsequent waiters
    /// acquire it, which can produce duplicate generator invocations under load. Set above your
    /// p99 generator runtime, or accept the partial herd as a trade-off.
    /// </summary>
    TimeSpan? DistributedLockExpiry { get; set; }

    /// <summary>
    /// Strategy that derives the Redis distributed-lock key from a cache key. The default
    /// strategy appends <c>":lck"</c> to <see cref="CacheKey.Name"/>.
    /// Note: the default does NOT apply your inner-cache prefix scheme (see
    /// <see cref="ICacheKeyStrategy"/>). Two applications sharing a Redis with the same
    /// <see cref="CacheKey.Name"/> have isolated values but COLLIDING lock keys. If you use
    /// a non-trivial cache-key strategy (e.g. <see cref="PrefixCacheKeyStrategy"/>), supply
    /// a matching lock-key strategy here too.
    /// </summary>
    IDistributedLockKeyStrategy? LockKeyStrategy { get; set; }
}

