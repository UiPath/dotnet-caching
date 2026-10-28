namespace UiPath.Caching;

/// <summary>What an inner write came to: whether the inner tier took it, the version it counted for a hash, and the version the local copy stands on.</summary>
internal readonly record struct InnerWrite(bool Written, VersionedWrite Counted, long Version);
