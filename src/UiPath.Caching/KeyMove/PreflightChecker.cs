using System.Net;

namespace UiPath.Caching.KeyMove;

/// <summary>Checks every master before a forward transition.</summary>
internal sealed class PreflightChecker(IStateTopology topology, KeyMoveStateOptions options)
{
    /// <param name="evictionApproved">A stored <c>Approve(control_records_evictable)</c> is present.</param>
    public async Task<PreflightResult> CheckAsync(bool evictionApproved, CancellationToken cancellationToken)
    {
        var failures = new List<PreflightFailure>();
        var layout = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
        foreach (var master in layout.Masters)
        {
            var probe = await topology.ProbeAsync(master.Endpoint, cancellationToken).ConfigureAwait(false);
            if (master.ConfiguredReplicas == MasterLayout.UnknownReplicas)
            {
                failures.Add(new PreflightFailure("replicas_unknown", $"{master.Endpoint}: the number of replicas is not configured"));
            }
            else if (probe.ConnectedReplicas < master.ConfiguredReplicas)
            {
                failures.Add(new PreflightFailure("replica_unhealthy", $"{master.Endpoint}: {probe.ConnectedReplicas} of {master.ConfiguredReplicas} replicas connected"));
            }

            CheckEviction(failures, master.Endpoint, probe, evictionApproved);
            await CheckProtocolAsync(failures, master.Endpoint, cancellationToken).ConfigureAwait(false);
        }

        return new PreflightResult(failures);
    }

    private static bool IsSafePolicy(string policy) =>
        policy.Equals("noeviction", StringComparison.OrdinalIgnoreCase) || policy.StartsWith("volatile-", StringComparison.OrdinalIgnoreCase);

    private void CheckEviction(List<PreflightFailure> failures, EndPoint master, MasterProbe probe, bool evictionApproved)
    {
        if (evictionApproved || options.EvictionPolicyAttested)
        {
            return;
        }

        if (probe.MaxmemoryPolicy is null)
        {
            failures.Add(new PreflightFailure("eviction_policy_unknown", $"{master}: the connection cannot read maxmemory-policy and nothing attests it"));
        }
        else if (!IsSafePolicy(probe.MaxmemoryPolicy))
        {
            failures.Add(new PreflightFailure("eviction_policy", $"{master}: {probe.MaxmemoryPolicy} can evict control records"));
        }
    }

    // Scripts or the 6.0.9 fallback, and a WAIT that answers on a dedicated connection together with the CLIENT ID and INFO that prove it ran on the connection that wrote.
    private async Task CheckProtocolAsync(List<PreflightFailure> failures, EndPoint master, CancellationToken cancellationToken)
    {
        try
        {
            await using var channel = await topology.OpenAsync(master, cancellationToken).ConfigureAwait(false);
            if (await channel.State.ScriptSupportAsync().ConfigureAwait(false) == ScriptSupport.Unsupported)
            {
                failures.Add(new PreflightFailure("scripts_unsupported", $"{master}: scripts are refused and the server is older than {MoveScripts.FallbackMinimum}"));
            }

            var server = channel.StateDatabase.Multiplexer.GetServer(master);
            await server.ExecuteAsync("WAIT", 0, 1L).WaitAsync(cancellationToken).ConfigureAwait(false);
            await server.ExecuteAsync("CLIENT", "ID").WaitAsync(cancellationToken).ConfigureAwait(false);
            await server.ExecuteAsync("INFO").WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException)
        {
            failures.Add(new PreflightFailure("wait_unavailable", $"{master}: {ex.Message}"));
        }
    }
}
