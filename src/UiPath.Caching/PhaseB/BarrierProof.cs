using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The server-side identity a barrier's write ran under: the client id, the server's run id and its replication id.</summary>
/// <remarks>
/// Equal proofs before and after a WAIT show that the WAIT ran on the connection that sent the write, to the same server process and the same replication
/// history. The replication id changes when a node is promoted after a full resync, which a surviving connection and an unchanged run id cannot show.
/// </remarks>
internal readonly record struct BarrierProof(long ClientId, string RunId, string ReplicationId)
{
    /// <summary>The arguments of the <c>INFO</c> that carries both ids, queued in the write's transaction and sent again after the WAIT.</summary>
    public static readonly string[] InfoSections = ["server", "replication"];

    /// <summary>The proof from the replies of <c>CLIENT ID</c> and <c>INFO server replication</c>; null when INFO lacks the run id or the replication id.</summary>
    public static async Task<BarrierProof?> OfAsync(Task<RedisResult> clientId, Task<RedisResult> info)
    {
        var text = (string?)await info.ConfigureAwait(false);
        var runId = Field(text, "run_id:");
        var replicationId = Field(text, "master_replid:");
        return runId is null || replicationId is null ? null : new BarrierProof((long)await clientId.ConfigureAwait(false), runId, replicationId);
    }

    private static string? Field(string? text, string name) =>
        text?.Split('\n').FirstOrDefault(l => l.StartsWith(name, StringComparison.Ordinal))?[name.Length..].Trim();
}
