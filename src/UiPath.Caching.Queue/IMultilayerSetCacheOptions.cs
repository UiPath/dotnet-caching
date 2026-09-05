namespace UiPath.Caching;

/// <summary>
/// Everything the multilayer set cache reads from its options: the memory tier's knobs through
/// <see cref="IMemoryCacheOptions"/>, plus the lifetime and connection settings that shape the local
/// snapshot in front of the backing set cache. Both queue options types implement it, so the cache
/// takes one object rather than each setting as a constructor argument.
/// </summary>
public interface IMultilayerSetCacheOptions : IMemoryCacheOptions
{
    /// <summary>
    /// Whole-set lifetime a write takes when neither the call nor the <see cref="CachePolicy"/> names
    /// one. <see langword="null"/> means "inherit", which resolves to
    /// <see cref="CachePolicy.DefaultDistributedExpiration"/>; <see cref="TimeSpan.MaxValue"/> keeps a
    /// set until it is removed.
    /// </summary>
    TimeSpan? DefaultExpiration { get; }

    /// <summary>
    /// Upper bound on how long a locally-cached snapshot is served before it is re-fetched from the
    /// backing tier. <see langword="null"/> caches snapshots without a time bound.
    /// </summary>
    TimeSpan? LocalMaxExpiration { get; }

    /// <summary>Monitors the backing tier's connection state. Required by <see cref="UseLocalOnlyWhenDisconnected"/>.</summary>
    bool ConnectionMonitorEnabled { get; }

    /// <summary>How often the connection monitor re-evaluates a failed connection.</summary>
    TimeSpan? ConnectionMonitorPeriod { get; }

    /// <summary>
    /// Serves reads from the local snapshot and applies mutations locally while the backing tier is
    /// unreachable. Requires <see cref="ConnectionMonitorEnabled"/>.
    /// </summary>
    bool UseLocalOnlyWhenDisconnected { get; }

    /// <summary>Upper bound on the lifetime of local state written while the backing tier is unreachable.</summary>
    TimeSpan? LocalMaxExpirationDisconnected { get; }
}
