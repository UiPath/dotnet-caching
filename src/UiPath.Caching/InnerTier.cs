using UiPath.Caching.Redis;

namespace UiPath.Caching;

/// <summary>What the cache under a multilayer cache is and can do, decided once when that cache is built.</summary>
internal abstract class InnerTier
{
    private protected InnerTier(bool isShared, ISharedTier? shared, ISharedHashTier? versioned, PeerFieldLimits peerLimits, IConnectionState? connection)
    {
        IsShared = isShared;
        Shared = shared;
        Versioned = versioned;
        PeerLimits = peerLimits;
        Connection = connection;
    }

    /// <summary>The tier that holds nothing: the local tier is the only one.</summary>
    public static InnerTier None { get; } = new NoInnerTier();

    /// <summary>Whether a distributed tier, shared with other nodes, sits below the local one.</summary>
    public bool IsShared { get; }

    /// <summary>Told of received invalidations and made to demand the master on reads, when the tier supports either.</summary>
    public ISharedTier? Shared { get; }

    /// <summary>Takes versioned writes, when peers update fields in place.</summary>
    public ISharedHashTier? Versioned { get; }

    /// <summary>The bounds of a field update sent to peers; meaningful only with <see cref="Versioned"/>.</summary>
    public PeerFieldLimits PeerLimits { get; }

    /// <summary>The tier's own connection state, when it reports one.</summary>
    public IConnectionState? Connection { get; }

    /// <summary>The null caches stand for no tier; any other cache is a shared (distributed) inner tier, Redis or any other cache.</summary>
    public static InnerTier For(object cache, IMultilayerCacheOptions? options = null) =>
        cache is NullCache or NullHashCache ? None : new SharedInnerTier(cache, options as InMemoryRedisCacheOptions);
}
