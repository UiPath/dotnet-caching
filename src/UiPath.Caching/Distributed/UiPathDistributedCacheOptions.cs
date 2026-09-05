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
    /// Differentiator handed to the Redis key strategy, placed after <c>AppShortName</c>, in the slot the
    /// application's own caches fill with a <see cref="RedisTypePrefixes"/> value. Null uses <c>"dh"</c>.
    /// Inert on the InMemory tier, and a value matching one of the application's type prefixes is rejected
    /// at registration.
    /// </summary>
    public string? RedisKeyDifferentiator { get; set; }

    /// <summary>
    /// Builds the Redis key from the composed <see cref="CacheKey"/>, receiving
    /// <see cref="RedisKeyDifferentiator"/>. Null uses the one the application configured on
    /// <see cref="RedisCacheOptions.RedisKeyStrategyFactory"/>, so the distributed cache inherits its
    /// <c>AppShortName</c>, separator and sharding conventions. Set this to take over the layout entirely —
    /// for a mandated key shape, or a cluster hash-tag scheme. Registration proves the resulting keys differ
    /// from the application's, so a factory that ignores the differentiator is rejected rather than silently
    /// sharing the keyspace.
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
