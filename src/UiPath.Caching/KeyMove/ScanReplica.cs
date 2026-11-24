using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>A replica of a scan unit's primary.</summary>
/// <param name="Name">The replica's identity: its cluster node id, or its endpoint.</param>
/// <param name="Server">The connected server, or null when the replica is known to the topology but not connected or flagged as failed.</param>
internal sealed record ScanReplica(string Name, IServer? Server);
