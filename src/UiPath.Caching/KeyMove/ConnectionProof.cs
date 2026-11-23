namespace UiPath.Caching.KeyMove;

/// <summary>The server-side identity of the connection a command ran on: the client id, the server's run id and its replication id.</summary>
/// <remarks>
/// Equal proofs before and after a WAIT show that the WAIT ran on the connection that sent the write, to the same server process, in the same replication
/// history: a master demoted, resynced and promoted again while the connection survived has a new replication id.
/// </remarks>
internal readonly record struct ConnectionProof(long ClientId, string RunId, string ReplicationId)
{
    /// <summary>Returns the INFO value for a field prefix such as <c>run_id:</c>.</summary>
    internal static string? InfoField(string? info, string name) =>
        info?.Split('\n').FirstOrDefault(l => l.StartsWith(name, StringComparison.Ordinal))?[name.Length..].Trim();
}
