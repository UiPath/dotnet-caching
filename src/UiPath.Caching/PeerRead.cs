namespace UiPath.Caching;

/// <summary>What a refresh read of one key found: <see cref="Entry"/> is set when <see cref="Found"/>.</summary>
internal readonly record struct PeerRead(bool Found, ICacheEntry? Entry);
