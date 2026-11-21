using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The barrier on the master that holds one owner key: it sends the writes and proves on the server that its WAIT ran on the connection that carried them.</summary>
/// <remarks>
/// <para>
/// Each attempt resolves the owner's master and its ROLE, then runs the writes in one transaction that begins with <c>CLIENT ID</c> and <c>INFO server replication</c>,
/// sent to that master only (no redirect). WAIT, then <c>CLIENT ID</c>, <c>INFO server replication</c> and ROLE run on the same server object afterwards. The
/// attempt is confirmed only for the same client id, the same run id, the same replication id and a master: WAIT counts the offset of the connection it
/// runs on, so a reconnect, a restart, or a demotion that resyncs and promotes again between the write and the WAIT shows in the proof, whatever events
/// the client has or has not delivered yet.
/// </para>
/// <para>
/// Anything short of that (an owner that moved, a failure, a MOVED or ASK answer, a WAIT of 0, a different proof) is not confirmed, and the next attempt
/// sends the whole transaction and WAIT again, up to <see cref="Retries"/> times with no extra backoff; a repeat must rewrite what it confirms. A way-back
/// transition sends its writes once without a WAIT for a master that failed within <see cref="Episode"/>.
/// </para>
/// <para>
/// <c>NoReplica</c> needs four facts: the deployment is configured without replicas, ROLE shows none, this instance never saw one, and the proof matches.
/// A ROLE that is denied leaves the replicas unknown: WAIT of 1 or more with a matching proof confirms, and it is never <c>NoReplica</c>. INFO needs a
/// connection with allowAdmin, and a server that takes several sections in one INFO (Redis 7.0 or later). Without allowAdmin nothing can be proven, so the
/// writes run once, unbound, and the outcome is <c>NotConfirmed</c>.
/// </para>
/// </remarks>
/// <param name="database">The view the owner key is routed through; WAIT runs on the master's own server connection.</param>
/// <param name="owner">A key routed to the server the barrier covers, for example a control record.</param>
/// <param name="clock">The monotonic clock of the episode.</param>
/// <param name="replicaFree">Configured: the deployment runs without replicas. Never inferred from ROLE.</param>
internal sealed class ServerBarrier(IDatabase database, RedisKey owner, TimeProvider clock, bool replicaFree)
{
    private const double TimeoutShare = 0.8;
    private const CommandFlags Master = CommandFlags.DemandMaster;

    // A bound write goes only to the master the client resolved: following a redirect could land it on a node its WAIT never asks.
    private const CommandFlags Pinned = CommandFlags.DemandMaster | CommandFlags.NoRedirect;
    private static readonly TimeSpan FallbackAsyncTimeout = TimeSpan.FromSeconds(5);

    private readonly bool _provable = AllowsAdmin(database.Multiplexer);
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

    /// <summary>Runs the writes in a transaction on the owner's master and returns once WAIT proves them on a replica, or the retries are spent.</summary>
    /// <param name="queueWrites">
    /// Queues the writes on the transaction it is given and returns without awaiting them before it executes; the task it returns, if it waits for the queued
    /// commands, is awaited after the transaction ran. It runs again on every attempt, on a new transaction.
    /// </param>
    /// <param name="wayBack">A way-back transition (X6, X7 to X10, an abandon).</param>
    /// <remarks>An error of the writes themselves (a refused command, a lost connection, a redirect, an EXECABORT) makes the attempt not confirmed and is never thrown.</remarks>
    public async Task<BarrierOutcome> WriteAndWaitAsync(Func<ITransaction, Task> queueWrites, bool wayBack)
    {
        var failedAt = Interlocked.Read(ref _failedAt);
        if (wayBack && failedAt != long.MinValue && clock.GetElapsedTime(failedAt) < Episode)
        {
            await WriteOnceAsync(queueWrites).ConfigureAwait(false);
            return BarrierOutcome.SkippedAfterRecentFailure;
        }

        if (!_provable)
        {
            await WriteOnceAsync(queueWrites).ConfigureAwait(false);
            Interlocked.Exchange(ref _failedAt, clock.GetTimestamp());
            return BarrierOutcome.NotConfirmed;
        }

        for (var attempt = 0; ; attempt++)
        {
            if (await AttemptAsync(queueWrites).ConfigureAwait(false) is { } outcome)
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

    private static bool Denies(Exception ex) =>
        ex is RedisServerException
        && (ex.Message.Contains("NOPERM", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("ACL failure", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the connection allows the admin commands INFO needs; false when its configuration cannot be read.</summary>
    private static bool AllowsAdmin(IConnectionMultiplexer multiplexer)
    {
        try
        {
            return ConfigurationOptions.Parse(multiplexer.Configuration).AllowAdmin;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

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

    /// <summary>Awaits the queued commands once the transaction committed; a transaction whose conditions failed ran nothing, so its commands are only observed.</summary>
    private static async Task SettleAsync(Task queued, bool committed)
    {
        try
        {
            await queued.ConfigureAwait(false);
        }
        catch (Exception) when (!committed)
        {
            // Cancelled with the transaction: nothing was applied, and the outcome says so.
        }
    }

    /// <summary>The writes in one transaction with no proof and no WAIT, for a connection that cannot prove one or a master that failed within the episode.</summary>
    private async Task WriteOnceAsync(Func<ITransaction, Task> queueWrites)
    {
        var transaction = database.CreateTransaction();
        var queued = queueWrites(transaction);
        var committed = await transaction.ExecuteAsync(Master).ConfigureAwait(false);
        await SettleAsync(queued, committed).ConfigureAwait(false);
    }

    private async Task<BarrierOutcome?> AttemptAsync(Func<ITransaction, Task> queueWrites)
    {
        try
        {
            if (await ResolveAsync().ConfigureAwait(false) is not { } target)
            {
                return null;
            }

            if (await WriteAsync(queueWrites).ConfigureAwait(false) is not { } sent)
            {
                return null;
            }

            var endpoint = await database.IdentifyEndpointAsync(owner, Master).ConfigureAwait(false);
            return Equals(endpoint, target.Server.EndPoint) ? await ConfirmAsync(target, sent).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
    }

    /// <summary>The owner's master and its replica count; null when it cannot be resolved or is not a master. The count is null when ROLE is denied.</summary>
    private async Task<Target?> ResolveAsync()
    {
        var endpoint = await database.IdentifyEndpointAsync(owner, Master).ConfigureAwait(false);
        if (endpoint is null)
        {
            return null;
        }

        var server = database.Multiplexer.GetServer(endpoint);
        try
        {
            return await server.RoleAsync().ConfigureAwait(false) is Role.Master master ? new Target(server, master.Replicas.Count) : null;
        }
        catch (Exception ex) when (Denies(ex))
        {
            // ROLE denied: the replicas are unknown, and WAIT alone decides.
            return new Target(server, null);
        }
    }

    /// <summary>The proof of the connection the writes ran on; null when the transaction did not commit.</summary>
    /// <remarks>CLIENT ID and INFO are queued first, so they run in the transaction of the writes: the connection that answers them is the one that wrote.</remarks>
    private async Task<BarrierProof?> WriteAsync(Func<ITransaction, Task> queueWrites)
    {
        var transaction = database.CreateTransaction();
        var clientId = transaction.ExecuteAsync("CLIENT", "ID");
        var info = transaction.ExecuteAsync("INFO", BarrierProof.InfoSections);
        var queued = queueWrites(transaction);
        var committed = await transaction.ExecuteAsync(Pinned).ConfigureAwait(false);
        await SettleAsync(queued, committed).ConfigureAwait(false);
        return committed ? await BarrierProof.OfAsync(clientId, info).ConfigureAwait(false) : null;
    }

    private async Task<BarrierOutcome?> ConfirmAsync(Target target, BarrierProof sent)
    {
        if (target.Replicas == 0)
        {
            return replicaFree && Volatile.Read(ref _sawReplicas) == 0 && await StillBoundAsync(target, sent).ConfigureAwait(false) ? BarrierOutcome.NoReplica : null;
        }

        if (target.Replicas > 0)
        {
            Volatile.Write(ref _sawReplicas, 1);
        }

        if ((long)await target.Server.ExecuteAsync("WAIT", 1, (long)Timeout.TotalMilliseconds).ConfigureAwait(false) < 1)
        {
            return null;
        }

        Volatile.Write(ref _sawReplicas, 1);
        return await StillBoundAsync(target, sent).ConfigureAwait(false) ? BarrierOutcome.Confirmed : null;
    }

    // The same client id and run id as the write's transaction, and still a master (unless ROLE is denied).
    private async Task<bool> StillBoundAsync(Target target, BarrierProof sent)
    {
        var server = target.Server;
        var now = await BarrierProof.OfAsync(server.ExecuteAsync("CLIENT", "ID"), server.ExecuteAsync("INFO", BarrierProof.InfoSections)).ConfigureAwait(false);
        return now == sent && (target.Replicas is null || await server.RoleAsync().ConfigureAwait(false) is Role.Master);
    }

    private readonly record struct Target(IServer Server, int? Replicas);
}
