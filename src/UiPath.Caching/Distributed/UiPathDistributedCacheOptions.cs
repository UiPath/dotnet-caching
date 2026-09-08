namespace UiPath.Caching.Distributed;

public class UiPathDistributedCacheOptions
{
    /// <summary>Prefix applied by the default <see cref="CacheKeyStrategy"/>.</summary>
    internal const string DefaultKeyPrefix = "d";

    /// <summary>Differentiator used by default for the Redis keyspace; see <see cref="RedisKeyDifferentiator"/>.</summary>
    internal const string DefaultRedisKeyDifferentiator = "dh";

    /// <summary>
    /// Composes the storage key from the caller key. Null applies
    /// <see cref="PrefixCacheKeyStrategy"/> with the prefix <c>"d"</c>, which is what keeps a
    /// distributed entry out of reach of the application's own <see cref="ICache"/>/<see cref="IHashCache"/>
    /// — including the local lock keyspace, which the memory tiers key by provider name plus cache key.
    /// Replacing it makes that separation yours to preserve; <see cref="DefaultCacheKeyStrategy"/> opts out
    /// of prefixing entirely.
    /// </summary>
    public ICacheKeyStrategy? CacheKeyStrategy { get; set; }

    /// <summary>
    /// Value placed after <c>AppShortName</c>, in the slot the application's caches fill with a
    /// <see cref="RedisKeyspaces"/> value. Null uses <c>"dh"</c>; inert on the InMemory tier. One segment of
    /// letters and digits, like every reserved keyspace. A value any package reserved is rejected at
    /// registration, in either order and even when caching is disabled.
    /// </summary>
    public string? RedisKeyDifferentiator { get; set; }

    /// <summary>
    /// Builds the Redis key from the composed <see cref="CacheKey"/>, receiving
    /// <see cref="RedisKeyDifferentiator"/>. Null uses the one the application configured on
    /// <see cref="RedisCacheOptions.RedisKeyStrategyFactory"/>, so the distributed cache inherits its
    /// <c>AppShortName</c>, separator and sharding conventions. Set this to take over the layout entirely.
    /// A layout that lands on the application's own keys, or on a reserved keyspace, is rejected when
    /// <c>IDistributedCache</c> is first resolved, which is startup only if something resolves it there.
    /// Ignoring the differentiator is fine as long as the keys stay disjoint. The check is a probe rather
    /// than a proof: reserved keyspaces are rendered through the application's factory, so a keyspace whose
    /// owner composes keys elsewhere, as broadcast streams do, is only approximated.
    /// </summary>
    public IRedisKeyStrategyFactory? RedisKeyStrategyFactory { get; set; }

    /// <summary>Optional <see cref="CachePolicy"/> name, resolved at registration; absent, the provider's default policy applies.</summary>
    public string? PolicyName { get; set; }

    /// <summary>
    /// Expiration applied when the caller supplies none. <see cref="IDistributedCache"/> treats absent
    /// expiration as "until removed"; unless <see cref="AllowUnboundedEntries"/> is set, that is mapped
    /// to this value so shared storage cannot accumulate unbounded keys. Null falls back to the backing
    /// tier's default expiration, and under that to
    /// <see cref="CachePolicy.DefaultDistributedExpiration"/>.
    /// </summary>
    public TimeSpan? DefaultEntryExpiration { get; set; }

    /// <summary>
    /// Honor "no expiration" literally instead of substituting a bounded default. Off by default, and
    /// now the only way to reach an unbounded entry through this adapter without naming a lifetime: a
    /// default left unset resolves to <see cref="CachePolicy.DefaultDistributedExpiration"/> rather
    /// than to "until removed".
    /// </summary>
    public bool AllowUnboundedEntries { get; set; }
}
