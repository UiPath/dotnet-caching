namespace UiPath.Caching;

/// <summary>What the cache under a multilayer cache is and can do, decided once when that cache is built.</summary>
internal abstract class InnerTier
{
    private protected InnerTier(bool isShared, IInvalidationSink? invalidations, IVersionedHashCache? versioned, PeerFieldLimits peerLimits, IConnectionState? connection)
    {
        IsShared = isShared;
        Invalidations = invalidations;
        Versioned = versioned;
        PeerLimits = peerLimits;
        Connection = connection;
    }

    /// <summary>The tier that holds nothing: the local tier is the only one.</summary>
    public static InnerTier None { get; } = new NoInnerTier();

    /// <summary>Whether a distributed tier, shared with other nodes, sits below the local one.</summary>
    public bool IsShared { get; }

    /// <summary>Told which keys a received event invalidated, when the tier tracks them.</summary>
    public IInvalidationSink? Invalidations { get; }

    /// <summary>Takes versioned writes, when peers update fields in place.</summary>
    public IVersionedHashCache? Versioned { get; }

    /// <summary>The bounds of a field update sent to peers; meaningful only with <see cref="Versioned"/>.</summary>
    public PeerFieldLimits PeerLimits { get; }

    /// <summary>The tier's own connection state, when it reports one.</summary>
    public IConnectionState? Connection { get; }

    /// <summary>The null caches stand for no tier; any other cache is a shared (distributed) inner tier, Redis or any other cache.</summary>
    public static InnerTier For(object cache, IMultilayerCacheOptions? options = null) =>
        cache is NullCache or NullHashCache ? None : new SharedInnerTier(cache, options as InMemoryRedisCacheOptions);
}
