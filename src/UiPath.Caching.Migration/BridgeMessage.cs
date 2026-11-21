namespace UiPath.Caching.Migration;

/// <summary>One invalidation on the older client's channel.</summary>
/// <param name="Kind">What is invalidated.</param>
/// <param name="CacheName">The router registration's name, for <see cref="BridgeMessageKind.Key"/> and <see cref="BridgeMessageKind.Clear"/>; the adapter maps the older client's names to it.</param>
/// <param name="Key">The caller key, for <see cref="BridgeMessageKind.Key"/>.</param>
/// <param name="Tag">The tag, for <see cref="BridgeMessageKind.Tag"/>.</param>
/// <param name="Origin">The identity of the sending node, when the format carries one; null when unknown.</param>
public readonly record struct BridgeMessage(BridgeMessageKind Kind, string? CacheName, string? Key = null, string? Tag = null, string? Origin = null);
