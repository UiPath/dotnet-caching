namespace UiPath.Platform.Caching.Config;

public static class CachePolicyMerger
{
    public static CachePolicy Merge(CachePolicy primary, CachePolicy fallback)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);
        return new()
        {
            LocalExpiration = primary.LocalExpiration ?? fallback.LocalExpiration,
            LocalExpirationDisconnected = primary.LocalExpirationDisconnected ?? fallback.LocalExpirationDisconnected,
            DistributedExpiration = primary.DistributedExpiration ?? fallback.DistributedExpiration,
            FactoryTimeout = primary.FactoryTimeout ?? fallback.FactoryTimeout,
            JitterMaxDuration = primary.JitterMaxDuration ?? fallback.JitterMaxDuration,
            RehydrateEnabled = primary.RehydrateEnabled ?? fallback.RehydrateEnabled,
            Rehydrate = primary.Rehydrate ?? fallback.Rehydrate,
            Lock = MergeLock(primary.Lock, fallback.Lock),
        };
    }

    internal static LockProfile? MergeLock(LockProfile? primary, LockProfile? fallback)
    {
        if (primary is null)
        {
            return fallback;
        }
        if (fallback is null)
        {
            return primary;
        }
        return new LockProfile
        {
            LocalLockEnabled = primary.LocalLockEnabled ?? fallback.LocalLockEnabled,
            DistributedLockEnabled = primary.DistributedLockEnabled ?? fallback.DistributedLockEnabled,
            LocalLockTimeout = primary.LocalLockTimeout ?? fallback.LocalLockTimeout,
            DistributedLockTimeout = primary.DistributedLockTimeout ?? fallback.DistributedLockTimeout,
            DistributedLockExpiry = primary.DistributedLockExpiry ?? fallback.DistributedLockExpiry,
        };
    }
}
