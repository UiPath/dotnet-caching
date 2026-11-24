using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>One unit of the mover's scan: a primary, or the server on a standalone deployment.</summary>
/// <param name="Id">The primary's stable identity: its cluster node id, or its endpoint on a standalone server. A failover or a reshard gives a new id, so the unit starts from zero.</param>
/// <param name="Primary">The unit's primary.</param>
/// <param name="Replica">A connected replica of the primary that the scan reads from; null when it has none, and the scan then reads the primary.</param>
internal sealed record ScanUnit(string Id, IServer Primary, IServer? Replica)
{
    /// <summary>The server a scan of the unit reads.</summary>
    public IServer ScanServer => Replica is { IsConnected: true } replica ? replica : Primary;
}
