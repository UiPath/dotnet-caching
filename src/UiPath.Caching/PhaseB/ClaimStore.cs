using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The app's claim record on one server: the five scripts, their fallback, the WAIT after OPEN, REOPEN and REL, and the admission read; give it a connection of its own.</summary>
internal sealed class ClaimStore
{
    /// <summary>How many times a fallback transaction is tried before the outcome is unknown (-7).</summary>
    internal const int FallbackAttempts = 5;

    private const CommandFlags Master = CommandFlags.DemandMaster;

    private const string ProbeScript = "return 1";

    // A bound write goes only to the master the client resolved: following a redirect could land it on a node its WAIT never asks.
    private const CommandFlags Pinned = CommandFlags.DemandMaster | CommandFlags.NoRedirect;

    private readonly IDatabase _db;
    private readonly Version? _serverVersion;
    private readonly bool _provable;
    private int _useScripts;
    private int _sawReplicas;
    private int _proofDenied;

    /// <param name="db">A database on a connection used for nothing else, since WAIT blocks it, with allowAdmin so a WAIT can be proven (INFO); without it no step is confirmed.</param>
    /// <param name="key">The claim key, from <see cref="MoveKeyNames.Claim"/>.</param>
    /// <param name="serverVersion">The oldest server version; the fallback needs 6.0.9 or later and never runs when this is unknown.</param>
    public ClaimStore(IDatabase db, RedisKey key, bool useScripts, Version? serverVersion)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        Key = key;
        _serverVersion = serverVersion;
        _useScripts = useScripts ? 1 : 0;
        _provable = ConfigurationOptions.Parse(db.Multiplexer.Configuration).AllowAdmin;
    }

    public RedisKey Key { get; }

    /// <summary>How many times an unconfirmed WAIT is retried by running the step again; 3 by default.</summary>
    public int BarrierRetries { get; init; } = 3;

    /// <summary>The WAIT timeout, 5 s by default; keep the connection's asyncTimeout above it, or a full-length WAIT fails as a client timeout.</summary>
    public TimeSpan BarrierTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool UsesScripts => Volatile.Read(ref _useScripts) == 1;

    /// <summary>Admission's read: HGETALL on the master, never a replica, so a confirmed step is always visible.</summary>
    public async Task<ClaimRecord> ReadAsync()
    {
        var entries = await _db.HashGetAllAsync(Key, Master).ConfigureAwait(false);
        return ClaimRecord.From(ToPairs(entries));
    }

    /// <param name="expectedPointer">For <c>fm</c>, the exact <c>cur:fm</c> value the gate read, or <c>-</c> when it was absent; empty for every other kind.</param>
    public Task<long> PendAsync(string field, string token, string descriptor, string expectedPointer) =>
        RunAsync(ClaimScripts.Pend, [field, token, descriptor, expectedPointer], record => ClaimLogic.Pend(record, field, token, descriptor, expectedPointer));

    public async Task<ClaimResult> OpenAsync(string field, string token, string descriptor, bool restore)
    {
        RedisValue[] args = [field, token, descriptor, restore ? "1" : "0"];
        return await DurableAsync(() => RunAsync(ClaimScripts.Open, args, record => ClaimLogic.Open(record, field, token, descriptor, restore), bound: true)).ConfigureAwait(false);
    }

    /// <summary>A certificate: <paramref name="swept"/> writes <c>S5</c> (a committed S5 with <c>lswept</c> stored), otherwise <c>S0</c>.</summary>
    public Task<ClaimResult> CertifyAsync(string field, string token, long epoch, bool swept) =>
        ReleaseAsync(field, token, "cert", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), swept ? "S5" : "S0");

    public Task<ClaimResult> IdleAsync(string field, string token) => ReleaseAsync(field, token, ClaimGrammar.Idle, string.Empty, string.Empty);

    public Task<ClaimResult> CloseAsync(string field, string token) => ReleaseAsync(field, token, ClaimGrammar.Closed, string.Empty, string.Empty);

    public Task<ClaimResult> ReopenAsync(string field, string token) =>
        DurableAsync(() => RunAsync(ClaimScripts.Reopen, [field, token], record => ClaimLogic.Reopen(record, field, token), bound: true));

    /// <summary>The operator's fenced delete: only while the field still holds <paramref name="expected"/>; never <c>cur:fm</c>.</summary>
    public Task<long> DropAsync(string field, string expected, string confirm) =>
        RunAsync(ClaimScripts.Drop, [field, expected, confirm], record => ClaimLogic.Drop(record, field, expected, confirm));

    internal Task<ClaimResult> ReleaseAsync(string field, string token, string target, string epoch, string terminal) =>
        DurableAsync(() => RunAsync(ClaimScripts.Release, [field, token, target, epoch, terminal], record => ClaimLogic.Release(record, field, token, target, epoch, terminal), bound: true));

    private static List<KeyValuePair<string, string>> ToPairs(HashEntry[] entries) =>
        [.. entries.Select(e => new KeyValuePair<string, string>((string)e.Name!, (string)e.Value!))];

    private static bool Transient(Exception ex) => ex is RedisException or RedisTimeoutException or ClaimNotAppliedException;

    // A key mid-migration answers ASK, and a slot that moved answers MOVED: the write did not run there, so the attempt starts over on the owner the client resolves next.
    private static bool Redirected(Exception ex) =>
        ex is RedisServerException && (ex.Message.StartsWith("MOVED ", StringComparison.Ordinal) || ex.Message.StartsWith("ASK ", StringComparison.Ordinal));

    // Discarded before it ran: a redirect, or an EXECABORT whose cause was not one this store can work around.
    private static bool NotApplied(Exception ex) => Redirected(ex) || ex is ClaimNotAppliedException;

    private static bool Aborted(Exception ex) => ex is RedisServerException && ex.Message.StartsWith("EXECABORT", StringComparison.Ordinal);

    /// <returns>The repeat when it rewrote the field; null when it failed and may be tried again; a code other than done when it no longer succeeds.</returns>
    private static async Task<ClaimStep?> RepeatAsync(Func<Task<ClaimStep>> step)
    {
        try
        {
            return await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
    }

    private static string? FieldOf(string? info, string name)
    {
        var line = info?.Split('\n').FirstOrDefault(l => l.StartsWith(name, StringComparison.Ordinal));
        return line?[name.Length..].Trim();
    }

    private static async Task<ConnectionProof?> ProofOf(Task<RedisResult> clientId, Task<RedisResult> info)
    {
        var text = (string?)await info.ConfigureAwait(false);
        return FieldOf(text, "run_id:") is { } runId && FieldOf(text, "master_replid:") is { } replicationId
            ? new ConnectionProof((long)await clientId.ConfigureAwait(false), runId, replicationId)
            : null;
    }

    /// <summary>A step, then WAIT on the connection its write went through; anything short of a confirmation repeats the whole write and WAIT, up to <see cref="BarrierRetries"/> times.</summary>
    /// <remarks>A repeat returns 2 and rewrites the field. After a success, a repeat that throws or no longer succeeds leaves the first code, not confirmed.</remarks>
    private async Task<ClaimResult> DurableAsync(Func<Task<ClaimStep>> step)
    {
        long? first = null;
        for (var attempt = 0; ; attempt++)
        {
            var target = await MasterAsync().ConfigureAwait(false);
            ConnectionProof? proof;
            if (first is null)
            {
                ClaimStep ran;
                try
                {
                    ran = await step().ConfigureAwait(false);
                }
                catch (Exception ex) when (NotApplied(ex) && attempt < BarrierRetries)
                {
                    // Nothing was written: the next attempt resolves the owner again.
                    continue;
                }

                first = ran.Code;
                proof = ran.Proof;
                if (first is not (ClaimCodes.Done or ClaimCodes.AlreadyDone))
                {
                    return new ClaimResult(first.Value, ClaimDurability.NotApplicable);
                }
            }
            else
            {
                var repeated = await RepeatAsync(step).ConfigureAwait(false);
                if (repeated is { Code: not (ClaimCodes.Done or ClaimCodes.AlreadyDone) })
                {
                    return new ClaimResult(first.Value, ClaimDurability.NotConfirmed);
                }

                proof = repeated?.Proof;
            }

            if (await BarrierAsync(target, proof).ConfigureAwait(false) is { } durability)
            {
                return new ClaimResult(first.Value, durability);
            }

            if (attempt >= BarrierRetries)
            {
                return new ClaimResult(first.Value, ClaimDurability.NotConfirmed);
            }
        }
    }

    /// <summary>Confirmed or NoReplica only when the server proves the WAIT ran on the connection that sent the write; null, which is not confirmed, otherwise.</summary>
    /// <remarks>
    /// The write's transaction returned its client id and the server's run id. After the WAIT both are read again on the same server, with ROLE: equal ids, the
    /// same run id and a master mean the same physical connection to the same process, whatever events the client has or has not delivered yet.
    /// A master whose replicas dropped to 0 after this store saw one is not confirmed, so the caller raises <c>wait_skipped{claim}</c>.
    /// </remarks>
    private async Task<ClaimDurability?> BarrierAsync((IServer Server, int Replicas)? target, ConnectionProof? proof)
    {
        if (target is not { } master || proof is not { } sent)
        {
            return null;
        }

        try
        {
            if (!Equals(await _db.IdentifyEndpointAsync(Key, Master).ConfigureAwait(false), master.Server.EndPoint))
            {
                return null;
            }

            if (master.Replicas == 0)
            {
                return Volatile.Read(ref _sawReplicas) == 0 && await StillBoundAsync(master.Server, sent).ConfigureAwait(false) ? ClaimDurability.NoReplica : null;
            }

            Volatile.Write(ref _sawReplicas, 1);
            if ((long)await master.Server.ExecuteAsync("WAIT", 1, (long)BarrierTimeout.TotalMilliseconds).ConfigureAwait(false) < 1)
            {
                return null;
            }

            return await StillBoundAsync(master.Server, sent).ConfigureAwait(false) ? ClaimDurability.Confirmed : null;
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
    }

    // The same client id, run id and replication id as the write's transaction, and still a master.
    private async Task<bool> StillBoundAsync(IServer server, ConnectionProof sent)
    {
        var now = await ProofOf(server.ExecuteAsync("CLIENT", "ID"), server.ExecuteAsync("INFO")).ConfigureAwait(false);
        return now == sent && await server.RoleAsync().ConfigureAwait(false) is Role.Master;
    }

    /// <summary>The claim's master and its replica count; null when it cannot be resolved, its role cannot be read, or it is not a master.</summary>
    private async Task<(IServer Server, int Replicas)?> MasterAsync()
    {
        try
        {
            var endpoint = await _db.IdentifyEndpointAsync(Key, Master).ConfigureAwait(false);
            if (endpoint is null)
            {
                return null;
            }

            var server = _db.Multiplexer.GetServer(endpoint);
            return await server.RoleAsync().ConfigureAwait(false) is Role.Master master ? (server, master.Replicas.Count) : null;
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
    }

    private async Task<long> RunAsync(string script, RedisValue[] args, Func<IReadOnlyList<KeyValuePair<string, string>>, ClaimDecision> decide) =>
        (await RunAsync(script, args, decide, bound: false).ConfigureAwait(false)).Code;

    /// <summary>The script, or once scripts are refused the same predicates over a snapshot and a watched transaction of the same writes.</summary>
    /// <remarks>
    /// <paramref name="bound"/> runs the write in one transaction with <c>CLIENT ID</c> and <c>INFO</c>, sent only to the master the client resolved
    /// (no redirect), so the step returns the proof of the connection that carried it. INFO needs a connection with allowAdmin; without it nothing is bound and
    /// the step is never confirmed.
    /// </remarks>
    private async Task<ClaimStep> RunAsync(string script, RedisValue[] args, Func<IReadOnlyList<KeyValuePair<string, string>>, ClaimDecision> decide, bool bound)
    {
        bound &= _provable && Volatile.Read(ref _proofDenied) == 0;
        var flags = bound ? Pinned : Master;
        if (UsesScripts)
        {
            try
            {
                if (!bound)
                {
                    return new((long)await _db.ScriptEvaluateAsync(script, [Key], args, flags).ConfigureAwait(false), null);
                }

                var transaction = _db.CreateTransaction();
                var clientId = transaction.ExecuteAsync("CLIENT", "ID");
                var info = transaction.ExecuteAsync("INFO");
                var result = transaction.ScriptEvaluateAsync(script, [Key], args, flags);
                try
                {
                    await transaction.ExecuteAsync(flags).ConfigureAwait(false);
                }
                catch (Exception ex) when (Aborted(ex))
                {
                    // Nothing ran; the cause decides between the fallback, an unbound step and a new attempt.
                    switch (await AbortCauseAsync(scripts: true, ex).ConfigureAwait(false))
                    {
                        case AbortCause.ProofDenied:
                            return await RunAsync(script, args, decide, bound: false).ConfigureAwait(false);
                        case AbortCause.ScriptsRefused:
                            throw new ScriptsRefusedSignal();
                    }
                }

                var code = (long)await result.ConfigureAwait(false);
                return new(code, await ProofOf(clientId, info).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is ScriptsRefusedSignal || RedisUtils.RefusesScript(ex))
            {
                Volatile.Write(ref _useScripts, 0);
            }
        }

        if (_serverVersion is null || _serverVersion < MoveScripts.FallbackMinimum)
        {
            throw new NotSupportedException($"The server refuses scripts and runs Redis {_serverVersion?.ToString() ?? "of an unknown version"}; the claim needs {MoveScripts.FallbackMinimum} or later without scripts.");
        }

        for (var attempt = 0; attempt < FallbackAttempts; attempt++)
        {
            var entries = await _db.HashGetAllAsync(Key, Master).ConfigureAwait(false);
            var decision = decide(ToPairs(entries));
            if (decision.Writes.Count == 0)
            {
                return new(decision.Code, null);
            }

            // The whole record as read, in raw bytes so a value that is not UTF-8 still compares equal: a change anywhere in it aborts the writes.
            var transaction = _db.CreateTransaction();
            transaction.AddCondition(Condition.HashLengthEqual(Key, entries.Length));
            foreach (var entry in entries)
            {
                transaction.AddCondition(Condition.HashEqual(Key, entry.Name, entry.Value));
            }

            var clientId = bound ? transaction.ExecuteAsync("CLIENT", "ID") : null;
            var info = bound ? transaction.ExecuteAsync("INFO") : null;
            var queued = new List<Task>(decision.Writes.Count);
            foreach (var write in decision.Writes)
            {
                queued.Add(write.Value is { } value
                    ? transaction.HashSetAsync(Key, write.Field, value, flags: flags)
                    : transaction.HashDeleteAsync(Key, write.Field, flags));
            }

            bool executed;
            try
            {
                executed = await transaction.ExecuteAsync(flags).ConfigureAwait(false);
            }
            catch (Exception ex) when (bound && Aborted(ex))
            {
                if (await AbortCauseAsync(scripts: false, ex).ConfigureAwait(false) == AbortCause.ProofDenied)
                {
                    return await RunAsync(script, args, decide, bound: false).ConfigureAwait(false);
                }

                throw;
            }

            if (executed)
            {
                await Task.WhenAll(queued).ConfigureAwait(false);
                return new(decision.Code, clientId is null ? null : await ProofOf(clientId, info!).ConfigureAwait(false));
            }
        }

        return new(ClaimCodes.FallbackExhausted, null);
    }

    /// <summary>Why a bound transaction was discarded: each part it queued is sent alone, once, to find the one the server refuses.</summary>
    /// <remarks>EXECABORT carries no cause and the client hides the queued command's error. Only a proof refused for good means no step can be proven, from now on.</remarks>
    private async Task<AbortCause> AbortCauseAsync(bool scripts, Exception abort)
    {
        try
        {
            await _db.ExecuteAsync("CLIENT", "ID").ConfigureAwait(false);
            await _db.ExecuteAsync("INFO").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisCommandException || RedisUtils.IsDenied(ex) || RedisUtils.IsUnsupportedCommand(ex))
        {
            // Only a refusal for good latches: a client without allowAdmin, an ACL denial or a command the server does not know.
            Volatile.Write(ref _proofDenied, 1);
            return AbortCause.ProofDenied;
        }
        catch (Exception ex) when (Transient(ex))
        {
            // BUSY, LOADING or a dropped connection says nothing about the proof: the attempt starts over.
            throw new ClaimNotAppliedException("The proof could not be read while the claim write was diagnosed; the write did not run.", ex);
        }

        try
        {
            if (scripts)
            {
                await _db.ScriptEvaluateAsync(ProbeScript, [Key], null, Pinned).ConfigureAwait(false);
            }
            else
            {
                await _db.KeyExistsAsync(Key, Pinned).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (Redirected(ex))
        {
            throw new ClaimNotAppliedException("The claim key was redirected; the write did not run.", ex);
        }
        catch (Exception ex) when (scripts && RedisUtils.RefusesScript(ex))
        {
            return AbortCause.ScriptsRefused;
        }

        throw new ClaimNotAppliedException("The server discarded the claim write for a reason its parts do not show when sent alone; the write did not run.", abort);
    }
}
