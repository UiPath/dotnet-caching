using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

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
/// sends the whole transaction and WAIT again, up to <see cref="Retries"/> times with no extra backoff; a repeat must rewrite what it confirms. An attempt
/// repeats whole in two cases. Either it certainly applied nothing: an EXECABORT, a redirect, a connection error before EXEC was sent, or a transaction
/// whose conditions failed. Or every transaction committed and the proof or the WAIT failed afterwards: the writes applied and are sent again by design,
/// which is why queued writes must be safe to repeat. An EXEC whose reply never arrived (a dropped connection, a timeout after sending) is uncertain: the outcome is <c>NotConfirmed</c>, the writes are
/// never replayed, and <see cref="LastError"/> holds the error. Once EXEC succeeded, a queued command that fails at run time means the transaction was partly applied: the outcome is <c>NotConfirmed</c>, the
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
    private const string ClientCommand = "CLIENT";
    private const string InfoCommand = "INFO";
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

    /// <summary>The error of the last write the barrier did not repeat: a queued command that failed at run time after EXEC succeeded, an EXEC whose reply never arrived, or an unbound write that failed; null when none.</summary>
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
    /// A batch whose transactions span masters goes through <see cref="WriteManyByKeyAndWaitAsync"/>, which splits it by master.
    /// An error of the writes themselves (a refused command, a lost connection, a redirect, an EXECABORT, a queued command that failed at run time) makes the
    /// attempt not confirmed and is never thrown. A run-time failure of a queued command after EXEC succeeded is not repeated, and is in <see cref="LastError"/>.
    /// </remarks>
    public Task<BarrierOutcome> WriteAndWaitAsync(Action<ITransaction> queueWrites, bool wayBack) => RunAsync([queueWrites], owner, wayBack, new Tally());

    /// <summary>Runs several transactions on the owner's master, each carrying keys of one slot, and returns once one WAIT proves all of them on a replica, or the retries are spent.</summary>
    /// <param name="writes">
    /// One transaction each, queued as in <see cref="WriteAndWaitAsync"/>. Every key of every transaction must be routed to the master of the owner key: the
    /// proof compares the client id of each transaction with the first one, so a transaction that ran on another connection shows as a different proof.
    /// </param>
    /// <param name="wayBack">A way-back transition (X6, X7 to X10, an abandon).</param>
    /// <remarks>
    /// The rules are those of <see cref="WriteAndWaitAsync"/>: every transaction begins with <c>CLIENT ID</c> and a plain <c>INFO</c>, all are sent to
    /// the pinned master, one WAIT follows, and the proof (client id of every transaction, run id, replication id, role master) is read again after it. An
    /// uncertain EXEC in any transaction is never replayed; a partly applied set is never replayed. A set repeats whole when it certainly applied nothing, or when
    /// every transaction committed and the proof or the WAIT failed afterwards; the writes must be safe to repeat, since that repeat sends every committed transaction again.
    /// </remarks>
    public Task<BarrierOutcome> WriteManyAndWaitAsync(IReadOnlyList<Action<ITransaction>> writes, bool wayBack)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
        {
            throw new ArgumentException("At least one transaction is needed.", nameof(writes));
        }

        return RunAsync(writes, owner, wayBack, new Tally());
    }

    /// <summary>Runs several transactions, each carrying keys of one slot, and returns once every master that holds them proved its part on a replica, or the retries are spent.</summary>
    /// <param name="writes">One keyed transaction each; the key says which master the transaction goes to, and is resolved when the call starts.</param>
    /// <param name="wayBack">A way-back transition (X6, X7 to X10, an abandon).</param>
    /// <remarks>
    /// The transactions are split by the master that owns their key: each master gets its own batch, its own WAIT and its own proof, and retries on its own with the rules of
    /// <see cref="WriteManyAndWaitAsync(IReadOnlyList{Action{ITransaction}}, bool)"/>. A key that cannot be resolved is sent through the client's routing and never confirms. The result is
    /// <c>Confirmed</c> only when every master's part is confirmed (<c>NoReplica</c> when every part is), <c>NotConfirmed</c> when any part is not.
    /// When one master committed a transaction of its part and another committed none, the batch is partly applied: the result is <c>NotConfirmed</c>,
    /// <see cref="LastError"/> holds the error, and the caller must never send the batch again, since that would replay the committed part.
    /// </remarks>
    public async Task<BarrierOutcome> WriteManyByKeyAndWaitAsync(IReadOnlyList<KeyedWrite> writes, bool wayBack)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
        {
            throw new ArgumentException("At least one transaction is needed.", nameof(writes));
        }

        var parts = await GroupWritesAsync(writes).ConfigureAwait(false);
        var outcomes = new List<BarrierOutcome>(parts.Count);
        var tallies = new List<Tally>(parts.Count);
        foreach (var part in parts)
        {
            var tally = new Tally();
            tallies.Add(tally);
            try
            {
                outcomes.Add(await RunAsync(part.Queues, part.Key, wayBack, tally).ConfigureAwait(false));
            }
            catch (Exception ex) when (tallies.Exists(static t => t.Committed))
            {
                // A part threw after a part committed: the batch is partly applied, and a repeat would replay the committed part. Cancellation still surfaces.
                Remember(new InvalidOperationException($"The keyed batch was partly applied: {tallies.Count(static t => t.Committed)} of {parts.Count} masters committed their part and the others did not, or may not have."));
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                return BarrierOutcome.NotConfirmed;
            }
        }

        return FinishKeyedBatch(tallies, outcomes, parts.Any(static p => p.Endpoint is null));
    }

    private static async Task<(bool[] Committed, Exception? Failure, Exception? Error)> ObserveWritesAsync(List<Sent> sent, Tally tally, Exception? failure)
    {
        var committed = new bool[sent.Count];
        Exception? uncertain = null;
        for (var i = 0; i < sent.Count; i++)
        {
            try
            {
                committed[i] = await sent[i].Exec.ConfigureAwait(false);
                tally.Committed |= committed[i];
            }
            catch (Exception ex) when (Transient(ex) && !NotSent(ex))
            {
                // The reply of EXEC never arrived: the writes may have applied, so they are never replayed.
                uncertain ??= ex;
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }

        Exception? error = uncertain;
        for (var i = 0; i < sent.Count; i++)
        {
            if (await SettleAsync(sent[i].Recorder.Queued(), committed[i]).ConfigureAwait(false) is { } queued)
            {
                error ??= queued;
            }
        }

        return (committed, failure, error);
    }

    // INFO proves the master role when ROLE is denied.
    private static async Task<bool> StillBoundAsync(Target target, Proven sent)
    {
        var server = target.Server;
        if (await ProofOfAsync(server.ExecuteAsync(ClientCommand, "ID"), server.ExecuteAsync(InfoCommand)).ConfigureAwait(false) is not { } now || now.Proof != sent.Proof)
        {
            return false;
        }

        return target.Replicas is null ? now.IsMaster : await server.RoleAsync().ConfigureAwait(false) is Role.Master;
    }

    private static async Task<Proven?> ReadWriteProofAsync(List<Sent> sent)
    {
        Proven? first = null;
        var master = true;
        foreach (var one in sent)
        {
            if (await ProofOfAsync(one.ClientId, one.Info).ConfigureAwait(false) is not { } proven || (first is { } head && proven.Proof != head.Proof))
            {
                return null;
            }

            first ??= proven;
            master &= proven.IsMaster;
        }

        return first is { } sole ? sole with { IsMaster = master } : null;
    }

    private static string? FieldOf(string? info, string name) =>
        info?.Split('\n').FirstOrDefault(l => l.StartsWith(name, StringComparison.Ordinal))?[name.Length..].Trim();

    /// <summary>The proof from the replies of <c>CLIENT ID</c> and <c>INFO</c>, and whether INFO says <c>role:master</c>; null when INFO lacks the run id or the replication id. A failed reply throws.</summary>
    private static InvalidOperationException PartlyApplied(int applied, int count) =>
        new($"The batch was partly applied: {applied} of {count} transactions committed and the others did not, or may not have.");

    private static async Task<Proven?> ProofOfAsync(Task<RedisResult> clientId, Task<RedisResult> info)
    {
        var text = (string?)await info.ConfigureAwait(false);
        return FieldOf(text, "run_id:") is { } runId && FieldOf(text, "master_replid:") is { } replicationId
            ? new Proven(new ConnectionProof((long)await clientId.ConfigureAwait(false), runId, replicationId), FieldOf(text, "role:") == "master")
            : null;
    }

    private static bool Transient(Exception ex) => ex is RedisException or RedisTimeoutException;

    /// <summary>Whether a failed EXEC certainly sent nothing that applied: a queue-time abort, a redirect, or a connection error before the command was sent.</summary>
    private static bool NotSent(Exception ex) => ex switch
    {
        RedisConnectionException connection => connection.CommandStatus == CommandStatus.WaitingToBeSent
            || connection.Message.Contains("No connection is active/available", StringComparison.OrdinalIgnoreCase),
        RedisServerException server => server.Message.StartsWith("EXECABORT", StringComparison.Ordinal)
            || server.Message.StartsWith("MOVED", StringComparison.Ordinal)
            || server.Message.StartsWith("ASK", StringComparison.Ordinal),
        _ => false,
    };

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

    private async Task<BarrierOutcome> RunAsync(IReadOnlyList<Action<ITransaction>> writes, RedisKey at, bool wayBack, Tally tally)
    {
        var failedAt = Interlocked.Read(ref _failedAt);
        if (wayBack && failedAt != long.MinValue && clock.GetElapsedTime(failedAt) < Episode)
        {
            await WriteOnceAsync(writes, tally).ConfigureAwait(false);
            return BarrierOutcome.SkippedAfterRecentFailure;
        }

        if (!_provable)
        {
            await WriteOnceAsync(writes, tally).ConfigureAwait(false);
            Interlocked.Exchange(ref _failedAt, clock.GetTimestamp());
            return BarrierOutcome.NotConfirmed;
        }

        var attempt = 0;
        while (true)
        {
            if (await AttemptAsync(writes, at, tally).ConfigureAwait(false) is { } outcome)
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

            attempt++;
        }
    }

    /// <summary>The writes in their transactions with no proof and no WAIT, for a connection that cannot prove one or a master that failed within the episode.</summary>
    /// <remarks>An error is kept in <see cref="LastError"/>, never thrown: the outcome of these paths does not depend on it.</remarks>
    private async Task WriteOnceAsync(IReadOnlyList<Action<ITransaction>> writes, Tally tally)
    {
        var applied = 0;
        try
        {
            foreach (var queueWrites in writes)
            {
                try
                {
                    var real = database.CreateTransaction();
                    queueWrites(TransactionRecorder.Wrap(real, out var recorder));
                    var committed = await real.ExecuteAsync(Master).ConfigureAwait(false);
                    tally.Committed |= committed;
                    applied += committed ? 1 : 0;
                    Remember(await SettleAsync(recorder.Queued(), committed).ConfigureAwait(false));
                }
                catch (Exception ex) when (Transient(ex))
                {
                    Remember(ex);
                }
            }
        }
        catch (Exception ex) when (applied > 0 && ex is not OperationCanceledException)
        {
            // A transaction threw after another committed: the rest are not sent, and the set is partly applied (below), never thrown. Cancellation still surfaces.
        }
        finally
        {
            if (applied > 0 && applied < writes.Count)
            {
                // One transaction committed while another had a condition fail, failed or was never sent: the set is partly applied and must never be sent again.
                Remember(PartlyApplied(applied, writes.Count));
            }
        }
    }

    private void Remember(Exception? error)
    {
        if (error is not null)
        {
            Volatile.Write(ref _lastError, error);
        }
    }

    private async Task<BarrierOutcome?> AttemptAsync(IReadOnlyList<Action<ITransaction>> writes, RedisKey at, Tally tally)
    {
        try
        {
            if (await ResolveAsync(at).ConfigureAwait(false) is not { } target)
            {
                return null;
            }

            var (sent, error) = await WriteAsync(writes, tally).ConfigureAwait(false);
            if (error is not null)
            {
                // A queued command failed after EXEC succeeded, or the reply of EXEC was lost: the writes may have applied, never replayed.
                Remember(error);
                return BarrierOutcome.NotConfirmed;
            }

            if (sent is not { } proven)
            {
                return null;
            }

            var endpoint = await database.IdentifyEndpointAsync(at, Master).ConfigureAwait(false);
            return Equals(endpoint, target.Server.EndPoint) ? await ConfirmAsync(target, proven).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
    }

    /// <summary>The owner's master and its replica count; null when it cannot be resolved or is not a master. The count is null when ROLE is denied.</summary>
    private async Task<Target?> ResolveAsync(RedisKey at)
    {
        var endpoint = await database.IdentifyEndpointAsync(at, Master).ConfigureAwait(false);
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

    private BarrierOutcome FinishKeyedBatch(List<Tally> tallies, List<BarrierOutcome> outcomes, bool ownerUnknown)
    {
        var applied = tallies.Count(static t => t.Committed);
        if (applied > 0 && applied < tallies.Count)
        {
            // One master committed its part while another committed nothing, or may not have: the batch is partly applied, and a repeat would replay the committed part.
            Remember(new InvalidOperationException($"The keyed batch was partly applied: {applied} of {tallies.Count} masters committed their part and the others did not."));
            return BarrierOutcome.NotConfirmed;
        }

        if (outcomes.Contains(BarrierOutcome.NotConfirmed) || ownerUnknown)
        {
            return BarrierOutcome.NotConfirmed;
        }

        if (outcomes.Contains(BarrierOutcome.SkippedAfterRecentFailure))
        {
            return BarrierOutcome.SkippedAfterRecentFailure;
        }

        return outcomes.All(static o => o == BarrierOutcome.NoReplica) ? BarrierOutcome.NoReplica : BarrierOutcome.Confirmed;
    }

    private async Task<List<(EndPoint? Endpoint, RedisKey Key, List<Action<ITransaction>> Queues)>> GroupWritesAsync(IReadOnlyList<KeyedWrite> writes)
    {
        var parts = new List<(EndPoint? Endpoint, RedisKey Key, List<Action<ITransaction>> Queues)>();
        foreach (var write in writes)
        {
            EndPoint? endpoint = null;
            try
            {
                endpoint = await database.IdentifyEndpointAsync(write.Key, Master).ConfigureAwait(false);
            }
            catch (Exception ex) when (Transient(ex))
            {
                // Unresolved: its part never confirms.
            }

            var index = parts.FindIndex(p => Equals(p.Endpoint, endpoint));
            if (index < 0)
            {
                parts.Add((endpoint, write.Key, [write.Queue]));
            }
            else
            {
                parts[index].Queues.Add(write.Queue);
            }
        }

        return parts;
    }

    /// <summary>The proof of the connection the writes ran on, null when a transaction did not commit or ran elsewhere; or the first error of a queued write that ran, of an EXEC that may have applied, or of a partly applied set.</summary>
    /// <remarks>
    /// CLIENT ID and INFO are queued first in every transaction, so they run with the writes: the connection that answers them is the one that wrote. The public client
    /// cannot pin a transaction to a chosen connection, so the batch holds by three facts: the endpoint resolved before and again after the writes, <c>NoRedirect</c> on every
    /// EXEC, and the full proof (client id, run id, replication id, role) of every transaction compared with the first one's and, after the WAIT, with the proof of the server.
    /// A set that certainly applied nothing repeats; one that committed while another failed or did not commit is partly applied, and is an error. A set that
    /// fully committed returns its proof, and the caller repeats it whole when that proof or the WAIT fails.
    /// </remarks>
    private async Task<(Proven? Proof, Exception? Error)> WriteAsync(IReadOnlyList<Action<ITransaction>> writes, Tally tally)
    {
        var (sent, failure) = DispatchWrites(writes);
        var (committed, finalFailure, error) = await ObserveWritesAsync(sent, tally, failure).ConfigureAwait(false);

        if (error is not null)
        {
            return (null, error);
        }

        var applied = committed.Count(static c => c);
        if (applied > 0 && applied < writes.Count)
        {
            // One transaction committed while another failed, was refused, had a condition fail or was never started: the set is partly applied, and a repeat would be a replay.
            return (null, PartlyApplied(applied, writes.Count));
        }

        if (finalFailure is not null)
        {
            // A redirect, an abort or a send that never happened for every transaction: nothing applied, and the whole set is sent again; anything else is not ours to hide.
            return Transient(finalFailure) ? (null, null) : throw finalFailure;
        }

        if (applied == 0)
        {
            return (null, null);
        }

        return (await ReadWriteProofAsync(sent).ConfigureAwait(false), null);
    }

    private (List<Sent> Sent, Exception? Failure) DispatchWrites(IReadOnlyList<Action<ITransaction>> writes)
    {
        var sent = new List<Sent>(writes.Count);
        Exception? failure = null;
        foreach (var queueWrites in writes)
        {
            try
            {
                var real = database.CreateTransaction();
                var clientId = real.ExecuteAsync(ClientCommand, "ID");
                var info = real.ExecuteAsync(InfoCommand);
                queueWrites(TransactionRecorder.Wrap(real, out var recorder));
                sent.Add(new Sent(clientId, info, recorder, real.ExecuteAsync(Pinned)));
            }
            catch (Exception ex)
            {
                // A synchronous throw while building or sending: this transaction never executed, and the rest are not started. The started ones are settled below
                // before anything is decided, since one of them may have committed or lost its reply.
                failure = ex;
                break;
            }
        }

        return (sent, failure);
    }

    private async Task<BarrierOutcome?> ConfirmAsync(Target target, Proven sent)
    {
        // A denied ROLE leaves INFO to say master, before the WAIT as after it.
        if (target.Replicas is null && !sent.IsMaster)
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

    private readonly record struct Proven(ConnectionProof Proof, bool IsMaster);

    private readonly record struct Target(IServer Server, int? Replicas);

    private sealed record Sent(Task<RedisResult> ClientId, Task<RedisResult> Info, TransactionRecorder Recorder, Task<bool> Exec);

    /// <summary>Whether any transaction of one run certainly committed, in any attempt.</summary>
    private sealed class Tally
    {
        public bool Committed { get; set; }
    }
}
