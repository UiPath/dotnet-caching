using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The barrier on the master that holds one owner key: WAIT confirms a write on the server that took it.</summary>
/// <remarks>
/// One replica or more confirms; 0, a timeout or an error re-issue the writes up to <see cref="Retries"/> times with no extra backoff. A way-back
/// transition skips at once for a master that failed within <see cref="Episode"/>. WAIT runs only on the endpoint that took the write: the caller resolves
/// it with <see cref="TargetAsync"/> before its write, and a master that changed since (a failover) is not confirmed.
/// <c>NoReplica</c> needs three facts: the deployment is configured without replicas, ROLE shows none, and this instance never saw one. ROLE lists
/// connected replicas only, so a replica that dropped, or a demoted master, is not confirmed: that is the case WAIT exists for. A ROLE that is denied or
/// fails leaves the replicas unknown: WAIT decides alone, and it is never <c>NoReplica</c>.
/// </remarks>
/// <param name="database">The view the owner key is routed through; WAIT runs on the master's own server connection.</param>
/// <param name="owner">A key routed to the server the barrier covers, for example a control record.</param>
/// <param name="clock">The monotonic clock of the episode.</param>
/// <param name="replicaFree">Configured: the deployment runs without replicas. Never inferred from ROLE.</param>
internal sealed class ServerBarrier(IDatabase database, RedisKey owner, TimeProvider clock, bool replicaFree)
{
    private const double TimeoutShare = 0.8;
    private static readonly TimeSpan FallbackAsyncTimeout = TimeSpan.FromSeconds(5);

    private long _failedAt = long.MinValue;
    private int _sawReplicas;
    private TimeSpan? _timeout;

    /// <summary>The WAIT timeout; by default 80% of the connection's async timeout, so WAIT returns before the client gives up on it.</summary>
    public TimeSpan Timeout
    {
        get => _timeout ?? DerivedTimeout(database.Multiplexer);
        init => _timeout = value;
    }

    public int Retries { get; init; } = 3;

    public TimeSpan Episode { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The endpoint of the master that holds the owner key now; null when it cannot be resolved. Call it before the write and pass it to <see cref="WaitAsync"/>.</summary>
    public async Task<EndPoint?> TargetAsync()
    {
        try
        {
            return await database.IdentifyEndpointAsync(owner, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
    }

    /// <param name="wroteTo">The endpoint that took the write, from <see cref="TargetAsync"/> before it; null when unknown, which is not confirmed.</param>
    /// <param name="reissue">Sends the writes again; a repeat must rewrite what it confirms, so the next WAIT is not vacuous.</param>
    /// <param name="wayBack">A way-back transition (X6, X7 to X10, an abandon).</param>
    public async Task<BarrierOutcome> WaitAsync(EndPoint? wroteTo, Func<Task> reissue, bool wayBack)
    {
        var failedAt = Interlocked.Read(ref _failedAt);
        if (wayBack && failedAt != long.MinValue && clock.GetElapsedTime(failedAt) < Episode)
        {
            return BarrierOutcome.SkippedAfterRecentFailure;
        }

        for (var attempt = 0; ; attempt++)
        {
            var target = attempt == 0 ? await MasterAsync(wroteTo).ConfigureAwait(false) : await ReissueOnTargetAsync(reissue).ConfigureAwait(false);
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

    /// <summary>The 80% of a connection's async timeout, from its configuration; 80% of 5 s when it cannot be read.</summary>
    private static TimeSpan DerivedTimeout(IConnectionMultiplexer multiplexer)
    {
        var asyncTimeout = FallbackAsyncTimeout;
        try
        {
            asyncTimeout = TimeSpan.FromMilliseconds(ConfigurationOptions.Parse(multiplexer.Configuration).AsyncTimeout);
        }
        catch (ArgumentException)
        {
            // An unreadable configuration keeps the default timeout of the client.
        }

        return TimeSpan.FromMilliseconds(Math.Max(1, Math.Floor(asyncTimeout.TotalMilliseconds * TimeoutShare)));
    }

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

    /// <summary>
    /// The writes sent again, on the master that held the owner key before, while it still holds it afterwards; null, which is not confirmed, when the
    /// target moved or the reissue failed.
    /// </summary>
    private async Task<(IServer Server, int? Replicas)?> ReissueOnTargetAsync(Func<Task> reissue)
    {
        var before = await TargetAsync().ConfigureAwait(false);
        return before is not null && await ReissueAsync(reissue).ConfigureAwait(false) ? await MasterAsync(before).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// The master of the owner key and its replica count, when it is <paramref name="wrote"/>; null when it cannot be resolved, differs from
    /// <paramref name="wrote"/>, or is not a master. The replica count is null when ROLE is denied or fails.
    /// </summary>
    private async Task<(IServer Server, int? Replicas)?> MasterAsync(EndPoint? wrote)
    {
        try
        {
            var endpoint = await TargetAsync().ConfigureAwait(false);
            if (endpoint is null || wrote is null || !Equals(endpoint, wrote))
            {
                return null;
            }

            var server = database.Multiplexer.GetServer(endpoint);
            try
            {
                return await server.RoleAsync().ConfigureAwait(false) is Role.Master master ? (server, master.Replicas.Count) : null;
            }
            catch (Exception ex) when (Transient(ex))
            {
                // ROLE denied or failed: the replicas are unknown, and WAIT alone decides.
                return (server, null);
            }
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
    }

    private async Task<BarrierOutcome?> ConfirmAsync((IServer Server, int? Replicas)? target)
    {
        if (target is not { } master)
        {
            return null;
        }

        if (master.Replicas == 0)
        {
            return replicaFree && Volatile.Read(ref _sawReplicas) == 0 ? BarrierOutcome.NoReplica : null;
        }

        if (master.Replicas > 0)
        {
            Volatile.Write(ref _sawReplicas, 1);
        }

        try
        {
            if ((long)await master.Server.ExecuteAsync("WAIT", 1, (long)Timeout.TotalMilliseconds).ConfigureAwait(false) < 1)
            {
                return null;
            }

            Volatile.Write(ref _sawReplicas, 1);
            return BarrierOutcome.Confirmed;
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
    }
}
