namespace UiPath.Platform.Caching.Config;

/// <summary>
/// Per-provider snapshot of <see cref="IMultilayerCacheOptions"/> as a <see cref="CachePolicy"/>.
/// Used by the <see cref="ICachePolicyFactory"/> singleton factory delegate at startup to validate
/// named-policy lock combinations against each registered provider's effective lock fields.
/// Resolved lazily there to avoid a DI cycle through <c>MultilayerCacheLockCrossOptionsValidator</c>.
/// Provider-specific defaults are NOT propagated through <c>ICachePolicyFactory.Default</c>; each
/// provider impl applies its own <see cref="IMultilayerCacheOptions"/> at call time.
/// </summary>
internal interface ICachePolicyDefaultBuilder
{
    string ProviderName { get; }

    CachePolicy Build();
}

internal abstract class BuildDefaultCachePolicyFromMultilayer<TSource> : ICachePolicyDefaultBuilder
    where TSource : class, IMultilayerCacheOptions
{
    private readonly IOptions<TSource> _source;

    protected BuildDefaultCachePolicyFromMultilayer(IOptions<TSource> source, string providerName)
    {
        _source = source;
        ProviderName = providerName;
    }

    public string ProviderName { get; }

    public CachePolicy Build()
    {
        var src = _source.Value;
        return new CachePolicy
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
}

internal sealed class InMemoryDefaultPolicyBuilder(IOptions<InMemoryCacheOptions> source)
    : BuildDefaultCachePolicyFromMultilayer<InMemoryCacheOptions>(source, KnownCacheProviderNames.InMemory)
{
}

internal sealed class InMemoryRedisDefaultPolicyBuilder(IOptions<InMemoryRedisCacheOptions> source)
    : BuildDefaultCachePolicyFromMultilayer<InMemoryRedisCacheOptions>(source, KnownCacheProviderNames.InMemoryRedis)
{
}
