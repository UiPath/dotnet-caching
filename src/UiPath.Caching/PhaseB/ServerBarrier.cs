using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The barrier on the master that holds one owner key: it sends the writes and proves on the server that its WAIT ran on the connection that carried them.</summary>
/// <remarks>
/// <para>
/// Each attempt resolves the owner's master and its ROLE, then runs the writes in one transaction that begins with <c>CLIENT ID</c> and <c>INFO</c>,
/// sent to that master only (no redirect). WAIT, then <c>CLIENT ID</c>, <c>INFO</c> and ROLE run on the same server object afterwards. The
/// attempt is confirmed only for the same client id, the same run id, the same replication id and a master: WAIT counts the offset of the connection it
/// runs on, so a reconnect, a restart, or a demotion that resyncs and promotes again between the write and the WAIT shows in the proof, whatever events
/// the client has or has not delivered yet.
/// </para>
/// <para>
/// Anything short of that (an owner that moved, a failure, a MOVED or ASK answer, a WAIT of 0, a different proof) is not confirmed, and the next attempt
/// sends the whole transaction and WAIT again, up to <see cref="Retries"/> times with no extra backoff; a repeat must rewrite what it confirms. Only an
/// attempt that certainly applied nothing repeats: an EXECABORT, a redirect, a connection error before EXEC completed, or a transaction whose conditions
/// failed. Once EXEC succeeded, a queued command that fails at run time means the transaction was partly applied: the outcome is <c>NotConfirmed</c>, the
/// writes are never replayed, and <see cref="LastError"/> holds the command's error. Every queued command is observed by the barrier, and all must
/// succeed before the outcome is confirmed. A way-back transition sends its writes once without a WAIT for a master that failed within <see cref="Episode"/>.
/// </para>
/// <para>
/// <c>NoReplica</c> needs four facts: the deployment is configured without replicas, ROLE shows none, this instance never saw one, and the proof matches.
/// A ROLE that is denied leaves the replicas unknown: WAIT of 1 or more with a matching proof confirms, and it is never <c>NoReplica</c>; the master check
/// then reads <c>role:master</c> from the INFO of the write's transaction and from the INFO after the WAIT. INFO needs a
/// connection with allowAdmin, and is sent without sections, since Redis 6 refuses more than one. Without allowAdmin nothing can be proven, so the
/// writes run once, unbound, and the outcome is <c>NotConfirmed</c>; their errors are handled as above, never thrown.
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
    private Exception? _lastError;

    /// <summary>The WAIT timeout; by default 80% of the connection's async timeout, so WAIT returns before the client gives up on it.</summary>
    public TimeSpan Timeout
    {
        get => _timeout ?? DerivedTimeout(database.Multiplexer);
        init => _timeout = value;
    }

    /// <summary>The error of the last write the barrier did not repeat: a queued command that failed at run time after EXEC succeeded, or an unbound write that failed; null when none.</summary>
    public Exception? LastError => Volatile.Read(ref _lastError);

    public int Retries { get; init; } = 3;

    public TimeSpan Episode { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Runs the writes in a transaction on the owner's master and returns once WAIT proves them on a replica, or the retries are spent.</summary>
    /// <param name="queueWrites">
    /// Queues the writes on the transaction it is given, without awaiting them: the barrier records the task of every command queued on it and observes each
    /// one after the transaction ran. It runs again on every attempt, on a new transaction, so the queued writes must be safe to repeat: an attempt that
    /// failed after some of its commands applied (a lost reply, a failed proof) is sent again whole.
    /// </param>
    /// <param name="wayBack">A way-back transition (X6, X7 to X10, an abandon).</param>
    /// <remarks>
    /// An error of the writes themselves (a refused command, a lost connection, a redirect, an EXECABORT, a queued command that failed at run time) makes the
    /// attempt not confirmed and is never thrown. A run-time failure of a queued command after EXEC succeeded is not repeated, and is in <see cref="LastError"/>.
    /// </remarks>
    public async Task<BarrierOutcome> WriteAndWaitAsync(Action<ITransaction> queueWrites, bool wayBack)
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
                if (outcome == BarrierOutcome.NotConfirmed)
                {
                    Interlocked.Exchange(ref _failedAt, clock.GetTimestamp());
                }

                return outcome;
            }

            if (attempt >= Retries)
            {
                Interlocked.Exchange(ref _failedAt, clock.GetTimestamp());
                return BarrierOutcome.NotConfirmed;
            }
        }
    }

    private static string? FieldOf(string? info, string name) =>
        info?.Split('\n').FirstOrDefault(l => l.StartsWith(name, StringComparison.Ordinal))?[name.Length..].Trim();

    /// <summary>The proof from the replies of <c>CLIENT ID</c> and <c>INFO</c>, and whether INFO says <c>role:master</c>; null when INFO lacks the run id or the replication id. A failed reply throws.</summary>
    private static async Task<Proven?> ProofOfAsync(Task<RedisResult> clientId, Task<RedisResult> info)
    {
        var text = (string?)await info.ConfigureAwait(false);
        return FieldOf(text, "run_id:") is { } runId && FieldOf(text, "master_replid:") is { } replicationId
            ? new Proven(new ConnectionProof((long)await clientId.ConfigureAwait(false), runId, replicationId), FieldOf(text, "role:") == "master")
            : null;
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

    /// <summary>Awaits every queued command; the first error of one that ran, else null. A transaction whose conditions failed ran nothing, so its commands are only observed.</summary>
    private static async Task<Exception?> SettleAsync(IReadOnlyList<Task> queued, bool committed)
    {
        try
        {
            await Task.WhenAll(queued).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Inspected below: after a commit each failure counts, otherwise the commands were cancelled with the transaction.
        }

        return committed && queued.FirstOrDefault(static t => t.Status != TaskStatus.RanToCompletion) is { } failed
            ? failed.Exception?.GetBaseException() ?? new OperationCanceledException("A queued command was cancelled after the transaction committed.")
            : null;
    }

    /// <summary>The writes in one transaction with no proof and no WAIT, for a connection that cannot prove one or a master that failed within the episode.</summary>
    /// <remarks>An error is kept in <see cref="LastError"/>, never thrown: the outcome of these paths does not depend on it.</remarks>
    private async Task WriteOnceAsync(Action<ITransaction> queueWrites)
    {
        try
        {
            var real = database.CreateTransaction();
            queueWrites(TransactionRecorder.Wrap(real, out var recorder));
            var committed = await real.ExecuteAsync(Master).ConfigureAwait(false);
            Remember(await SettleAsync(recorder.Queued(), committed).ConfigureAwait(false));
        }
        catch (Exception ex) when (Transient(ex))
        {
            Remember(ex);
        }
    }

    private void Remember(Exception? error)
    {
        if (error is not null)
        {
            Volatile.Write(ref _lastError, error);
        }
    }

    private async Task<BarrierOutcome?> AttemptAsync(Action<ITransaction> queueWrites)
    {
        try
        {
            if (await ResolveAsync().ConfigureAwait(false) is not { } target)
            {
                return null;
            }

            var (sent, error) = await WriteAsync(queueWrites).ConfigureAwait(false);
            if (error is not null)
            {
                // EXEC succeeded and a queued command failed: the transaction is partly applied, never replayed.
                Remember(error);
                return BarrierOutcome.NotConfirmed;
            }

            if (sent is not { } proven)
            {
                return null;
            }

            var endpoint = await database.IdentifyEndpointAsync(owner, Master).ConfigureAwait(false);
            return Equals(endpoint, target.Server.EndPoint) ? await ConfirmAsync(target, proven).ConfigureAwait(false) : null;
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

    /// <summary>The proof of the connection the writes ran on, null when the transaction did not commit; or the first error of a queued write that ran.</summary>
    /// <remarks>CLIENT ID and INFO are queued first, so they run in the transaction of the writes: the connection that answers them is the one that wrote.</remarks>
    private async Task<(Proven? Proof, Exception? Error)> WriteAsync(Action<ITransaction> queueWrites)
    {
        var real = database.CreateTransaction();
        var clientId = real.ExecuteAsync("CLIENT", "ID");
        var info = real.ExecuteAsync("INFO");
        queueWrites(TransactionRecorder.Wrap(real, out var recorder));
        var committed = await real.ExecuteAsync(Pinned).ConfigureAwait(false);
        if (await SettleAsync(recorder.Queued(), committed).ConfigureAwait(false) is { } error)
        {
            return (null, error);
        }

        return (committed ? await ProofOfAsync(clientId, info).ConfigureAwait(false) : null, null);
    }

    private async Task<BarrierOutcome?> ConfirmAsync(Target target, Proven sent)
    {
        // A denied ROLE leaves INFO to say master, before the WAIT as after it.
        if (target.Replicas is null && !sent.Master)
        {
            return null;
        }

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

    // The same client id, run id and replication id as the write's transaction, and still a master: by ROLE, or by INFO when ROLE is denied.
    private async Task<bool> StillBoundAsync(Target target, Proven sent)
    {
        var server = target.Server;
        if (await ProofOfAsync(server.ExecuteAsync("CLIENT", "ID"), server.ExecuteAsync("INFO")).ConfigureAwait(false) is not { } now || now.Proof != sent.Proof)
        {
            return false;
        }

        return target.Replicas is null ? now.Master : await server.RoleAsync().ConfigureAwait(false) is Role.Master;
    }

    private readonly record struct Proven(ConnectionProof Proof, bool Master);

    private readonly record struct Target(IServer Server, int? Replicas);
}
