namespace UiPath.Caching.Broadcast;

/// <summary>The typed event properties the cache reads from a decoded event; an event format has to bring each back as the type noted here.</summary>
public static class CacheEventPropertyNames
{
    /// <summary>A <see cref="long"/>: the count a hash write reached.</summary>
    public const string Version = "_version_";

    /// <summary>A <see cref="DateTimeOffset"/>.</summary>
    public const string Expiration = "_expiration_";

    /// <summary>A <see cref="DateTimeOffset"/>: the expiry of a peer-update write.</summary>
    public const string PeerExpiration = "_peerExpiration_";

    /// <summary>An <c>IEnumerable&lt;KeyValuePair&lt;string, string?&gt;&gt;</c>.</summary>
    public const string Metadata = "_metadata_";

    /// <summary>An <c>IEnumerable&lt;KeyValuePair&lt;string, string?&gt;&gt;</c>: the metadata of a peer-update write.</summary>
    public const string PeerMetadata = "_peerMetadata_";

    /// <summary>An <c>IEnumerable&lt;KeyValuePair&lt;string, string?&gt;&gt;</c> or <c>IEnumerable&lt;KeyValuePair&lt;string, byte[]?&gt;&gt;</c>: the fields a peer-update write changed.</summary>
    public const string PeerFields = "_peerFields_";
}
