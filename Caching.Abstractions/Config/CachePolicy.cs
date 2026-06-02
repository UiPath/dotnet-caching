namespace UiPath.Platform.Caching;

/// <summary>
/// Per-cache-instance settings resolved by <see cref="ICachePolicyFactory"/>. Each
/// <see cref="ICache{T}"/> / <see cref="IHashCache{T}"/> binds a policy by name at construction.
/// A field set to <c>null</c> always means "inherit from the next level down" (named policy →
/// default policy → library default); to explicitly disable a feature, set the relevant boolean
/// to <c>false</c>.
/// </summary>
public sealed class CachePolicy
{
    /// <summary>
    /// Per-policy L1 (in-memory tier) cap. Applied at <c>SetAsync</c> / <c>GetOrAddAsync</c> write
    /// time when the L2 is connected, falling back to <see cref="IMultilayerCacheOptions.LocalMaxExpiration"/>
    /// if null. L1 effective TTL is <c>min(entry.Expiration, LocalExpiration)</c>. Naming aligns
    /// with .NET HybridCache's Local/Distributed tier vocabulary.
    /// </summary>
    public TimeSpan? LocalExpiration { get; init; }

    /// <summary>
    /// Per-policy L1 cap used when the L2 is disconnected (connection monitor reports unhealthy).
    /// Falls back to <see cref="IMultilayerCacheOptions.LocalMaxExpirationDisconnected"/> if null.
    /// </summary>
    public TimeSpan? LocalExpirationDisconnected { get; init; }

    /// <summary>
    /// Per-policy L2 lifetime. Used as the entry expiration on <c>SetAsync</c> / <c>GetOrAddAsync</c>
    /// when the caller doesn't pass an explicit <c>expiration</c> argument, falling back to
    /// <see cref="ICacheOptions.DefaultExpiration"/>. Also drives the rehydrate trigger's "Duration"
    /// for soft-TTL math.
    /// </summary>
    public TimeSpan? DistributedExpiration { get; init; }

    public TimeSpan? FactoryTimeout { get; init; }

    /// <summary>
    /// Upper bound on the random duration added to the L2 (distributed) expiration at write time —
    /// uniform random in <c>[0, JitterMaxDuration)</c> added to the resolved write duration on
    /// <c>SetAsync</c> / <c>GetOrAddAsync</c> / <c>RefreshAsync</c>. The resolved write duration is
    /// <see cref="DistributedExpiration"/> or, if null, <c>IMultilayerCacheOptions.DefaultExpiration</c>.
    /// Applies only when the caller does not pass an explicit <c>expiration</c> argument (caller-supplied
    /// values are honored exactly). Spreads cluster-wide expirations after bulk writes (e.g. deploy
    /// warm-up). The L1 cap (<see cref="LocalExpiration"/> /
    /// <c>IMultilayerCacheOptions.LocalMaxExpiration</c>) is unaffected in the typical case where it is
    /// less than or equal to the resolved L2 duration; if the L1 cap is configured longer, the L2 entry's
    /// jittered absolute expiration flows through to the in-memory entry's absolute expiration — per-node
    /// only, no cluster-sync concern. Bulk <c>SetAsync(KeyValuePair[])</c> writes share a single jittered
    /// TTL across the batch (one draw per call), which still spreads expirations across nodes but does
    /// not vary within the batch. <c>null</c> or <see cref="TimeSpan.Zero"/> disables jitter. A
    /// <c>JitterMaxDuration</c> larger than <see cref="DistributedExpiration"/> is accepted but produces
    /// wildly skewed TTLs — pick a value smaller than the typical lifetime.
    /// </summary>
    public TimeSpan? JitterMaxDuration { get; init; }

    /// <summary>
    /// Master switch for proactive background refresh. Rehydration fires only when this is
    /// <c>true</c> <em>and</em> <see cref="Rehydrate"/> carries tuning settings. <c>null</c>
    /// inherits from the next level down (default <em>off</em>).
    /// </summary>
    public bool? RehydrateEnabled { get; init; }

    /// <summary>
    /// Rehydrate tuning. Merged whole-object: a named policy that sets <c>Rehydrate</c> replaces the
    /// default's <c>Rehydrate</c> entirely (no per-field merge). To override a single field like
    /// <see cref="RehydrateOptions.Threshold"/>, redeclare the full <see cref="RehydrateOptions"/>
    /// in the named policy. (Compare with <see cref="Lock"/>, which is field-level merged.)
    /// </summary>
    public RehydrateOptions? Rehydrate { get; init; }

    /// <summary>
    /// Lock settings. Merged field-level against the default policy's <see cref="LockProfile"/> —
    /// a named policy can override individual fields (e.g. <see cref="LockProfile.LocalLockEnabled"/>)
    /// while inheriting the rest. (Compare with <see cref="Rehydrate"/>, which is replaced wholesale.)
    /// </summary>
    public LockProfile? Lock { get; init; }
}
