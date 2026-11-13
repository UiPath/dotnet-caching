namespace UiPath.Caching;

/// <summary>What a peer's set event says its write changed: the hash's write count it reached, and the fields, lifetime and metadata of the write.</summary>
internal sealed record PeerUpdate(long Version, IReadOnlyDictionary<string, byte[]?> Fields, DateTimeOffset? Expiration, IDictionary<string, string?>? Metadata);
