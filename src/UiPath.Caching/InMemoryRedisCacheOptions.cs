using UiPath.Caching.Locking;

namespace UiPath.Caching;

public class InMemoryRedisCacheOptions : IMultilayerCacheOptions, IMemoryCacheOptions
{
    internal const int DefaultMaxBackgroundWrites = 1024;

    internal const int DefaultBatchChunkSize = 500;

    internal const int DefaultBatchConcurrentChunks = 2;

    internal const int DefaultPeerFieldUpdateMaxFields = 16;

    internal const int DefaultPeerFieldUpdateMaxBytes = 1024;

    internal static readonly TimeSpan DefaultBackgroundWriteDrainTimeout = TimeSpan.FromSeconds(5);

    public bool Enabled { get; set; } = true;

    public TimeSpan? DefaultExpiration { get; set; } = CachePolicy.DefaultDistributedExpiration;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);

    public ICacheEntryFactory? EntryFactory { get; set; }

    public ICacheKeyStrategy? CacheKeyStrategy { get; set; }

    public bool TrackStatistics { get; set; } = true;

    public TimeSpan StatisticsFlushInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Publishes and consumes cross-node L1 invalidations for this provider. Set to <c>false</c> to run
    /// L1+L2 with no broadcast traffic, which is what you want on a single node or against a Redis-compatible
    /// store whose Streams support does not cover <c>XREADGROUP</c>. Defaults to <c>true</c>; note this is the
    /// opposite of <see cref="InMemoryCacheOptions.BroadcastEnable"/>, which is opt-in.
    /// <para>
    /// This flag can only narrow the app-wide setting, never widen it. The effective behavior is
    /// <see cref="CacheOptions.BroadcastEnabled"/> AND this property, so when broadcast is off app-wide
    /// setting this to <c>true</c> has no effect.
    /// </para>
    /// </summary>
    public bool BroadcastEnable { get; set; } = true;

    public string? Topic { get; set; }

    public ITopicKeyStrategy? TopicKeyStrategy { get; set; }

    public TimeSpan? LocalMaxExpiration { get; set; }

    public bool? ConnectionMonitorEnabled { get; set; }

    /// <summary>
    /// Persists generator-returned nulls and empty hashes as sentinels. <c>AddInMemoryRedis</c> propagates
    /// this flag to the inner <c>RedisCacheOptions.CacheNullValues</c>; custom <c>MultilayerCache</c>-over-
    /// <c>RedisCache</c> compositions must keep both options in sync themselves.
    /// </summary>
    public bool CacheNullValues { get; set; }

    public TimeSpan? ConnectionMonitorPeriod { get; set; } = TimeSpan.FromSeconds(5);

    public long? SizeLimit { get; set; }

    public double? CompactionPercentage { get; set; }

    public ICacheEntrySizeProvider? SizeProvider { get; set; }

    public bool? UseLocalOnlyWhenDisconnected { get; set; }

    public bool? ClearLocalOnReconnect { get; set; }

    public TimeSpan? LocalMaxExpirationDisconnected { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many background writes (<see cref="CachePolicy.BackgroundDistributedWrite"/>) may run at once; one that finds none free is written in the foreground. Zero turns background writes off.</summary>
    public int MaxBackgroundWrites { get; set; } = DefaultMaxBackgroundWrites;

    /// <summary>How long disposing the cache waits for background writes still running before it cancels them; <see cref="Timeout.InfiniteTimeSpan"/> waits for them all.</summary>
    public TimeSpan BackgroundWriteDrainTimeout { get; set; } = DefaultBackgroundWriteDrainTimeout;

    /// <summary>How many keys <c>RemoveManyAsync</c> and <c>SetManyAsync</c> put on the wire at once; the input is taken in chunks of this size, so memory and the wait for one reply stay bounded however many keys there are.</summary>
    public int BatchChunkSize { get; set; } = DefaultBatchChunkSize;

    /// <summary>How many chunks of a batch call are in flight at once; one chunk's events go out while the next chunk's commands are sent.</summary>
    public int BatchConcurrentChunks { get; set; } = DefaultBatchConcurrentChunks;

    /// <summary>Above this many keys (an input of known size), a batch call sends one topic-wide event after its writes instead of one per key; null keeps one per key.</summary>
    /// <remarks>Every node then drops the local tier of the topic, for every cache that shares it, since events name no cache. An older node ignores the event and keeps its copies until they expire, so set it only once every node knows it.</remarks>
    public int? BatchTopicInvalidationThreshold { get; set; }

    /// <summary>A small <c>HashReplace</c> write tells the other nodes which fields it wrote, and a node holding the hash adds them to its copy instead of dropping it, when its copy is exactly one write behind; any other copy is dropped.</summary>
    /// <remarks>Every write of a hash then also counts the hash's writes in a reserved <c>_version_</c> field. Set it only once every node that writes the hashes knows it: a write by a node that does not count leaves the copies of the others to be dropped by its event.</remarks>
    public bool PeerFieldUpdates { get; set; }

    /// <summary>The most fields a write may carry to the other nodes; a write of more is dropped by them.</summary>
    public int PeerFieldUpdateMaxFields { get; set; } = DefaultPeerFieldUpdateMaxFields;

    /// <summary>The most bytes the names and serialized values of a write's fields may add up to for the write to be carried to the other nodes.</summary>
    public int PeerFieldUpdateMaxBytes { get; set; } = DefaultPeerFieldUpdateMaxBytes;

    // Declared before the new pair: the configuration binder sets properties in declaration order, so the new key wins when both are set.
    [Obsolete("Use LocalSingleFlightEnabled; the in-process lock it named is gone.")]
    public bool? LocalLockEnabled { get => LocalSingleFlightEnabled; set => LocalSingleFlightEnabled = value; }

    [Obsolete("Use LocalSingleFlightTimeout; the in-process lock it named is gone.")]
    public TimeSpan? LocalLockTimeout { get => LocalSingleFlightTimeout; set => LocalSingleFlightTimeout = value; }

    public bool? LocalSingleFlightEnabled { get; set; } = true;

    public TimeSpan? LocalSingleFlightTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    public bool? DistributedLockEnabled { get; set; }

    public TimeSpan? DistributedLockTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan? DistributedLockExpiry { get; set; } = TimeSpan.FromSeconds(5);

    public IDistributedLockKeyStrategy? LockKeyStrategy { get; set; }

    /// <summary>Member-wise copy, so a caller can vary one setting without mutating the DI singleton.</summary>
    internal InMemoryRedisCacheOptions ShallowCopy() => (InMemoryRedisCacheOptions)MemberwiseClone();
}

