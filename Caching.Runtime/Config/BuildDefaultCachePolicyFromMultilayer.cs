namespace UiPath.Platform.Caching.Config;

/// <summary>
/// Snapshots <see cref="IMultilayerCacheOptions"/> as a <see cref="CachePolicy"/>. Used by
/// <see cref="MultilayerCacheBase"/> to merge the provider-specific defaults with the
/// user-configured generic <see cref="ICachePolicyFactory.Default"/>.
/// </summary>
internal static class CachePolicyFromMultilayerOptions
{
    public static CachePolicy Build(IMultilayerCacheOptions src) =>
        new()
        {
            LocalExpiration = src.LocalMaxExpiration,
            LocalExpirationDisconnected = src.LocalMaxExpirationDisconnected,
            DistributedExpiration = src.DefaultExpiration,
            Lock = new LockProfile
            {
                LocalLockEnabled = src.LocalLockEnabled,
                LocalLockTimeout = src.LocalLockTimeout,
                DistributedLockEnabled = src.DistributedLockEnabled,
                DistributedLockTimeout = src.DistributedLockTimeout,
                DistributedLockExpiry = src.DistributedLockExpiry,
            },
        };
}
