using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The app's claim record on one server: the five scripts, their fallback, the WAIT after OPEN, REOPEN and REL, and the admission read; give it a connection of its own.</summary>
internal sealed class ClaimStore
{
    /// <summary>How many times a fallback transaction is tried before the outcome is unknown (-7).</summary>
    internal const int FallbackAttempts = 5;

    private const CommandFlags Master = CommandFlags.DemandMaster;

    private readonly IDatabase _db;
    private readonly Version? _serverVersion;
    private int _useScripts;
    private int _sawReplicas;

    /// <param name="db">A database on a connection used for nothing else, since WAIT blocks it.</param>
    /// <param name="key">The claim key, from <see cref="MoveKeyNames.Claim"/>.</param>
    /// <param name="serverVersion">The oldest server version; the fallback needs 6.0.9 or later and never runs when this is unknown.</param>
    public ClaimStore(IDatabase db, RedisKey key, bool useScripts, Version? serverVersion)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        Key = key;
        _serverVersion = serverVersion;
        _useScripts = useScripts ? 1 : 0;
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
        return await DurableAsync(() => RunAsync(ClaimScripts.Open, args, record => ClaimLogic.Open(record, field, token, descriptor, restore))).ConfigureAwait(false);
    }

    /// <summary>A certificate: <paramref name="swept"/> writes <c>S5</c> (a committed S5 with <c>lswept</c> stored), otherwise <c>S0</c>.</summary>
    public Task<ClaimResult> CertifyAsync(string field, string token, long epoch, bool swept) =>
        ReleaseAsync(field, token, "cert", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), swept ? "S5" : "S0");

    public Task<ClaimResult> IdleAsync(string field, string token) => ReleaseAsync(field, token, ClaimGrammar.Idle, string.Empty, string.Empty);

    public Task<ClaimResult> CloseAsync(string field, string token) => ReleaseAsync(field, token, ClaimGrammar.Closed, string.Empty, string.Empty);

    public Task<ClaimResult> ReopenAsync(string field, string token) =>
        DurableAsync(() => RunAsync(ClaimScripts.Reopen, [field, token], record => ClaimLogic.Reopen(record, field, token)));

    /// <summary>The operator's fenced delete: only while the field still holds <paramref name="expected"/>; never <c>cur:fm</c>.</summary>
    public Task<long> DropAsync(string field, string expected, string confirm) =>
        RunAsync(ClaimScripts.Drop, [field, expected, confirm], record => ClaimLogic.Drop(record, field, expected, confirm));

    internal Task<ClaimResult> ReleaseAsync(string field, string token, string target, string epoch, string terminal) =>
        DurableAsync(() => RunAsync(ClaimScripts.Release, [field, token, target, epoch, terminal], record => ClaimLogic.Release(record, field, token, target, epoch, terminal)));

    private static List<KeyValuePair<string, string>> ToPairs(HashEntry[] entries) =>
        [.. entries.Select(e => new KeyValuePair<string, string>((string)e.Name!, (string)e.Value!))];

    private static bool Transient(Exception ex) => ex is RedisException or RedisTimeoutException;

    /// <returns>True when the repeat rewrote the field, false when it no longer succeeds, null when it failed and may be tried again.</returns>
    private static async Task<bool?> RepeatAsync(Func<Task<long>> step)
    {
        try
        {
            return await step().ConfigureAwait(false) is ClaimCodes.Done or ClaimCodes.AlreadyDone;
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
    }

    /// <summary>A step, then WAIT on the master its write reached; anything short of a confirmation repeats it, up to <see cref="BarrierRetries"/> times.</summary>
    /// <remarks>A repeat returns 2 and rewrites the field. After a success, a repeat that throws or no longer succeeds leaves the first code, not confirmed.</remarks>
    private async Task<ClaimResult> DurableAsync(Func<Task<long>> step)
    {
        long? first = null;
        for (var attempt = 0; ; attempt++)
        {
            var target = await MasterAsync().ConfigureAwait(false);
            if (first is null)
            {
                first = await step().ConfigureAwait(false);
                if (first is not (ClaimCodes.Done or ClaimCodes.AlreadyDone))
                {
                    return new ClaimResult(first.Value, ClaimDurability.NotApplicable);
                }
            }
            else
            {
                var repeated = await RepeatAsync(step).ConfigureAwait(false);
                if (repeated == false)
                {
                    return new ClaimResult(first.Value, ClaimDurability.NotConfirmed);
                }

                if (repeated is null)
                {
                    target = null;
                }
            }

            if (await BarrierAsync(target).ConfigureAwait(false) is { } durability)
            {
                return new ClaimResult(first.Value, durability);
            }

            if (attempt >= BarrierRetries)
            {
                return new ClaimResult(first.Value, ClaimDurability.NotConfirmed);
            }
        }
    }

    /// <summary>Confirmed or NoReplica while the master is the one resolved before the write; null, which is not confirmed, otherwise.</summary>
    /// <remarks>A master whose replicas dropped to 0 after this store saw one is not confirmed, so the caller raises <c>wait_skipped{claim}</c>.</remarks>
    private async Task<ClaimDurability?> BarrierAsync((IServer Server, int Replicas)? target)
    {
        if (target is not { } master)
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
                return Volatile.Read(ref _sawReplicas) == 1 ? null : ClaimDurability.NoReplica;
            }

            Volatile.Write(ref _sawReplicas, 1);
            return (long)await master.Server.ExecuteAsync("WAIT", 1, (long)BarrierTimeout.TotalMilliseconds).ConfigureAwait(false) >= 1 ? ClaimDurability.Confirmed : null;
        }
        catch (Exception ex) when (Transient(ex))
        {
            return null;
        }
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

    /// <summary>The script, or once scripts are refused the same predicates over a snapshot and a watched transaction of the same writes.</summary>
    private async Task<long> RunAsync(string script, RedisValue[] args, Func<IReadOnlyList<KeyValuePair<string, string>>, ClaimDecision> decide)
    {
        if (UsesScripts)
        {
            try
            {
                return (long)await _db.ScriptEvaluateAsync(script, [Key], args, Master).ConfigureAwait(false);
            }
            catch (Exception ex) when (RedisUtils.RefusesScript(ex))
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
                return decision.Code;
            }

            // The whole record as read, in raw bytes so a value that is not UTF-8 still compares equal: a change anywhere in it aborts the writes.
            var transaction = _db.CreateTransaction();
            transaction.AddCondition(Condition.HashLengthEqual(Key, entries.Length));
            foreach (var entry in entries)
            {
                transaction.AddCondition(Condition.HashEqual(Key, entry.Name, entry.Value));
            }

            var queued = new List<Task>(decision.Writes.Count);
            foreach (var write in decision.Writes)
            {
                queued.Add(write.Value is { } value
                    ? transaction.HashSetAsync(Key, write.Field, value, flags: Master)
                    : transaction.HashDeleteAsync(Key, write.Field, Master));
            }

            if (await transaction.ExecuteAsync(Master).ConfigureAwait(false))
            {
                await Task.WhenAll(queued).ConfigureAwait(false);
                return decision.Code;
            }
        }

        return ClaimCodes.FallbackExhausted;
    }
}
