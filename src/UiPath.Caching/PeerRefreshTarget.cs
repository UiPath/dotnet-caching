namespace UiPath.Caching;

/// <summary>How a held entry is refreshed when a peer writes its key: the typed reader, and the policy the entry was kept under.</summary>
internal readonly record struct PeerRefreshTarget(IPeerRefreshReader Reader, CachePolicy Policy);
