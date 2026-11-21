using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// The barrier on the master that holds one owner key: one replica or more confirms; 0, a timeout or an error re-issue the writes up to
/// <see cref="Retries"/> times with no extra backoff. A way-back transition skips at once for a master that failed within <see cref="Episode"/>.
/// </summary>
/// <remarks>
/// <c>NoReplica</c> needs three facts: the deployment is configured without replicas, ROLE shows none, and this instance never saw one. ROLE lists
/// connected replicas only, so a replica that dropped, or a demoted master, is not confirmed: that is the case WAIT exists for.
/// </remarks>
/// <param name="database">The view the owner key is routed through; WAIT runs on the master's own server connection.</param>
/// <param name="owner">A key routed to the server the barrier covers, for example a control record.</param>
/// <param name="clock">The monotonic clock of the episode.</param>
/// <param name="replicaFree">Configured: the deployment runs without replicas. Never inferred from ROLE.</param>
internal sealed class ServerBarrier(IDatabase database, RedisKey owner, TimeProvider clock, bool replicaFree)
{
    private long _failedAt = long.MinValue;
    private int _sawReplicas;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public int Retries { get; init; } = 3;

    public TimeSpan Episode { get; init; } = TimeSpan.FromMinutes(10);

    /// <param name="reissue">Sends the writes again; a repeat must rewrite what it confirms, so the next WAIT is not vacuous.</param>
    /// <param name="wayBack">A way-back transition (X6, X7 to X10, an abandon).</param>
    public async Task<BarrierOutcome> WaitAsync(Func<Task> reissue, bool wayBack)
    {
        var failedAt = Interlocked.Read(ref _failedAt);
        if (wayBack && failedAt != long.MinValue && clock.GetElapsedTime(failedAt) < Episode)
        {
            return BarrierOutcome.SkippedAfterRecentFailure;
        }

        for (var attempt = 0; ; attempt++)
        {
            var target = await MasterAsync().ConfigureAwait(false);
            if (attempt > 0)
            {
                target = await ReissueAsync(reissue).ConfigureAwait(false) ? await UnchangedAsync(target).ConfigureAwait(false) : null;
            }

            if (await ConfirmAsync(target).ConfigureAwait(false) is { } outcome)
            {
                return outcome;
            }

            if (attempt >= Retries)
            {
                Interlocked.Exchange(ref _failedAt, clock.GetTimestamp());
                return BarrierOutcome.NotConfirmed;
            }
        }
    }

    private static bool Transient(Exception ex) => ex is RedisException or RedisTimeoutException;

    /// <summary>False when the reissue failed on the server or the connection (a failover), which is not confirmed, never an exception.</summary>
    private static async Task<bool> ReissueAsync(Func<Task> reissue)
    {
        try
        {
            await reissue().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (Transient(ex))
        {
            return false;
        }
    }

    /// <summary>The master of the owner key and its replica count; null when it cannot be resolved, its role cannot be read, or it is not a master.</summary>
    private async Task<(IServer Server, int Replicas)?> MasterAsync()
    {
        try
        {
            var endpoint = await database.IdentifyEndpointAsync(owner, CommandFlags.DemandMaster).ConfigureAwait(false);
            if (endpoint is null)
            {
                return null;
            }

            var server = database.Multiplexer.GetServer(endpoint);
            return await server.RoleAsync().ConfigureAwait(false) is Role.Master master ? (server, master.Replicas.Count) : null;
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
    }

    /// <summary>The target while the owner key still routes to it after the writes were sent again; null, which is not confirmed, otherwise.</summary>
    private async Task<(IServer Server, int Replicas)?> UnchangedAsync((IServer Server, int Replicas)? target)
    {
        if (target is not { } master)
        {
            return null;
        }

        try
        {
            return Equals(await database.IdentifyEndpointAsync(owner, CommandFlags.DemandMaster).ConfigureAwait(false), master.Server.EndPoint) ? master : null;
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
    }

    private async Task<BarrierOutcome?> ConfirmAsync((IServer Server, int Replicas)? target)
    {
        if (target is not { } master)
        {
            return null;
        }

        if (master.Replicas == 0)
        {
            return replicaFree && Volatile.Read(ref _sawReplicas) == 0 ? BarrierOutcome.NoReplica : null;
        }

        Volatile.Write(ref _sawReplicas, 1);
        try
        {
            return (long)await master.Server.ExecuteAsync("WAIT", 1, (long)Timeout.TotalMilliseconds).ConfigureAwait(false) >= 1 ? BarrierOutcome.Confirmed : null;
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
    }
}
