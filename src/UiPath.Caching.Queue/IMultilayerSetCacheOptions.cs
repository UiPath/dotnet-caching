namespace UiPath.Caching;

/// <summary>What <c>MultilayerSetCache</c> reads from its options; both queue options types implement it.</summary>
public interface IMultilayerSetCacheOptions : IMemoryCacheOptions
{
    /// <summary>Whole-set lifetime when neither the call nor the policy names one. <see langword="null"/> resolves to <see cref="CachePolicy.DefaultDistributedExpiration"/>; <see cref="TimeSpan.MaxValue"/> keeps the set until removed.</summary>
    TimeSpan? DefaultExpiration { get; }

    /// <summary>Cap on how long a local snapshot is served before it is re-fetched; <see langword="null"/> is no cap.</summary>
    TimeSpan? LocalMaxExpiration { get; }

    /// <summary>Monitors the backing tier's connection state. Required by <see cref="UseLocalOnlyWhenDisconnected"/>.</summary>
    bool ConnectionMonitorEnabled { get; }

    /// <summary>How often the connection monitor re-evaluates a failed connection.</summary>
    TimeSpan? ConnectionMonitorPeriod { get; }

    /// <summary>Serve and mutate the local snapshot while the backing tier is unreachable; requires <see cref="ConnectionMonitorEnabled"/>.</summary>
    bool UseLocalOnlyWhenDisconnected { get; }

    /// <summary>Upper bound on the lifetime of local state written while the backing tier is unreachable.</summary>
    TimeSpan? LocalMaxExpirationDisconnected { get; }
}
