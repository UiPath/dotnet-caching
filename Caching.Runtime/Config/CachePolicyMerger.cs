namespace UiPath.Platform.Caching.Config;

internal static class CachePolicyMerger
{
    public static CachePolicy Merge(CachePolicy named, CachePolicy defaults) =>
        new()
        {
            LocalExpiration = named.LocalExpiration ?? defaults.LocalExpiration,
            LocalExpirationDisconnected = named.LocalExpirationDisconnected ?? defaults.LocalExpirationDisconnected,
            DistributedExpiration = named.DistributedExpiration ?? defaults.DistributedExpiration,
            FactoryTimeout = named.FactoryTimeout ?? defaults.FactoryTimeout,
            JitterMaxDuration = named.JitterMaxDuration ?? defaults.JitterMaxDuration,
            RehydrateEnabled = named.RehydrateEnabled ?? defaults.RehydrateEnabled,
            Rehydrate = named.Rehydrate ?? defaults.Rehydrate,
            Lock = MergeLock(named.Lock, defaults.Lock),
        };

    internal static LockProfile? MergeLock(LockProfile? named, LockProfile? defaults)
    {
        if (named is null)
        {
            return defaults;
        }
        if (defaults is null)
        {
            return named;
        }
        return new LockProfile
        {
            LocalLockEnabled = named.LocalLockEnabled ?? defaults.LocalLockEnabled,
            DistributedLockEnabled = named.DistributedLockEnabled ?? defaults.DistributedLockEnabled,
            LocalLockTimeout = named.LocalLockTimeout ?? defaults.LocalLockTimeout,
            DistributedLockTimeout = named.DistributedLockTimeout ?? defaults.DistributedLockTimeout,
            DistributedLockExpiry = named.DistributedLockExpiry ?? defaults.DistributedLockExpiry,
        };
    }
}
