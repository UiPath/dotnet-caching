using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>One unit of the mover's scan: a primary, or the server on a standalone deployment.</summary>
/// <param name="Id">The primary's stable identity: its cluster node id, or its endpoint on a standalone server. A failover or a reshard gives a new id, so the unit starts from zero.</param>
/// <param name="Primary">The unit's primary.</param>
/// <param name="Replica">A connected replica of the primary that the scan reads from; null when it has none, and the scan then reads the primary.</param>
internal sealed record ScanUnit(string Id, IServer Primary, IServer? Replica)
{
    /// <summary>Every replica the topology names for the primary, connected or not; empty when it names none.</summary>
    public IReadOnlyList<ScanReplica> Replicas { get; init; } = [];

    /// <summary>The server a scan of the unit reads.</summary>
    public IServer ScanServer => Replica is { IsConnected: true } replica ? replica : Primary;

    /// <summary>The replicas to judge: <see cref="Replicas"/>, or <see cref="Replica"/> alone when the unit lists none.</summary>
    public IReadOnlyList<ScanReplica> ReplicasToJudge
    {
        get
        {
            if (Replicas.Count > 0)
            {
                return Replicas;
            }

            return Replica is { } replica ? [new ScanReplica(Id + "/replica", replica)] : [];
        }
    }
}
