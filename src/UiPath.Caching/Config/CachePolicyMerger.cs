namespace UiPath.Caching.Config;

public static class CachePolicyMerger
{
    private static readonly CachePolicy Blank = new();

    [return: NotNullIfNotNull(nameof(primary))]
    [return: NotNullIfNotNull(nameof(fallback))]
    public static CachePolicy? Merge(CachePolicy? primary, CachePolicy? fallback)
    {
        if (primary is null)
        {
            return fallback;
        }
        if (fallback is null)
        {
            return primary;
        }
        return Combine(primary, fallback, null);
    }

    internal static CachePolicy WithWriteSerializer(CachePolicy policy, ICacheValueSerializer writeSerializer) =>
        Combine(policy, Blank, writeSerializer);

    internal static ICacheValueSerializer? WriteSerializerOf(CachePolicy policy) => policy.WriteSerializer;

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
            LocalSingleFlightEnabled = primary.LocalSingleFlightEnabled ?? fallback.LocalSingleFlightEnabled,
            DistributedLockEnabled = primary.DistributedLockEnabled ?? fallback.DistributedLockEnabled,
            LocalSingleFlightTimeout = primary.LocalSingleFlightTimeout ?? fallback.LocalSingleFlightTimeout,
            DistributedLockTimeout = primary.DistributedLockTimeout ?? fallback.DistributedLockTimeout,
            DistributedLockExpiry = primary.DistributedLockExpiry ?? fallback.DistributedLockExpiry,
            DistributedLockSkipReread = primary.DistributedLockSkipReread ?? fallback.DistributedLockSkipReread,
        };
    }

    private static SerializerPolicy? MergeSerializer(SerializerPolicy? primary, SerializerPolicy? fallback)
    {
        if (primary is null)
        {
            return fallback;
        }
        if (fallback is null)
        {
            return primary;
        }
        return new SerializerPolicy
        {
            Write = primary.Write ?? fallback.Write,
            Read = primary.Read ?? fallback.Read,
            MigrateOnRead = primary.MigrateOnRead ?? fallback.MigrateOnRead,
        };
    }

    // The resolved slot belongs to the name it was resolved from, so it follows whichever side supplies Write.
    private static CachePolicy Combine(CachePolicy primary, CachePolicy fallback, ICacheValueSerializer? writeSerializer)
    {
        return new()
        {
            LocalExpiration = primary.LocalExpiration ?? fallback.LocalExpiration,
            LocalExpirationDisconnected = primary.LocalExpirationDisconnected ?? fallback.LocalExpirationDisconnected,
            DistributedExpiration = primary.DistributedExpiration ?? fallback.DistributedExpiration,
            FactoryTimeout = primary.FactoryTimeout ?? fallback.FactoryTimeout,
            BackgroundDistributedWrite = primary.BackgroundDistributedWrite ?? fallback.BackgroundDistributedWrite,
            RefreshLocalOnPeerWrite = primary.RefreshLocalOnPeerWrite ?? fallback.RefreshLocalOnPeerWrite,
            RefreshLocalRetryWindow = primary.RefreshLocalRetryWindow ?? fallback.RefreshLocalRetryWindow,
            JitterMaxDuration = primary.JitterMaxDuration ?? fallback.JitterMaxDuration,
            RehydrateEnabled = primary.RehydrateEnabled ?? fallback.RehydrateEnabled,
            Rehydrate = primary.Rehydrate ?? fallback.Rehydrate,
            Lock = MergeLock(primary.Lock, fallback.Lock),
            FailSafeEnabled = primary.FailSafeEnabled ?? fallback.FailSafeEnabled,
            FailSafeMaxStaleAge = primary.FailSafeMaxStaleAge ?? fallback.FailSafeMaxStaleAge,
            FailSafeCooldown = primary.FailSafeCooldown ?? fallback.FailSafeCooldown,
            FailSafeTimeout = primary.FailSafeTimeout ?? fallback.FailSafeTimeout,
            Serializer = MergeSerializer(primary.Serializer, fallback.Serializer),
            WriteSerializer = writeSerializer
                ?? (primary.Serializer?.Write is not null
                    ? primary.WriteSerializer
                    : primary.WriteSerializer ?? fallback.WriteSerializer),
        };
    }
}
