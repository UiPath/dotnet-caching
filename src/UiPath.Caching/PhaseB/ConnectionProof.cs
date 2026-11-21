namespace UiPath.Caching.PhaseB;

/// <summary>The server-side identity of the connection a command ran on: the client id, the server's run id and its replication id.</summary>
/// <remarks>
/// Equal proofs before and after a WAIT show that the WAIT ran on the connection that sent the write, to the same server process, in the same replication
/// history: a master demoted, resynced and promoted again while the connection survived has a new replication id.
/// </remarks>
internal readonly record struct ConnectionProof(long ClientId, string RunId, string ReplicationId);
