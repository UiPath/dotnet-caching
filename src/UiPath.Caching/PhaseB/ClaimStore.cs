using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// The app's claim record on one server: the five scripts, their fallback when scripts are refused, the WAIT that follows OPEN, REOPEN and REL, and the
/// admission read. Give it a connection of its own: WAIT blocks the connection it runs on.
/// </summary>
internal sealed class ClaimStore
{
    /// <summary>How many times a fallback transaction is tried before the outcome is unknown (-7).</summary>
    internal const int FallbackAttempts = 5;

    private const CommandFlags Master = CommandFlags.DemandMaster;

    private readonly IDatabase _db;
    private readonly Version? _serverVersion;
    private int _useScripts;

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

    public TimeSpan BarrierTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool UsesScripts => Volatile.Read(ref _useScripts) == 1;

    /// <summary>The record from the master, parsed by the shared grammar: what admission reads.</summary>
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

    private static async Task<int> ReplicasAsync(IServer server) =>
        await server.RoleAsync().ConfigureAwait(false) is Role.Master master ? master.Replicas.Count : 0;

    /// <summary>
    /// A step that returned 1 or 2, then WAIT on the claim's master: one replica or more confirms; 0, a timeout or an error repeat the step (it returns 2
    /// and rewrites the field, so the next WAIT covers it) up to <see cref="BarrierRetries"/> times. A master with no replica needs no WAIT.
    /// </summary>
    private async Task<ClaimResult> DurableAsync(Func<Task<long>> step)
    {
        var code = await step().ConfigureAwait(false);
        if (code is not (ClaimCodes.Done or ClaimCodes.AlreadyDone))
        {
            return new ClaimResult(code, ClaimDurability.NotApplicable);
        }

        var server = _db.Multiplexer.GetServer(await _db.IdentifyEndpointAsync(Key, Master).ConfigureAwait(false) ?? throw new RedisException("The claim's master is unknown."));
        for (var attempt = 0; ; attempt++)
        {
            if (await ReplicasAsync(server).ConfigureAwait(false) == 0)
            {
                return new ClaimResult(code, ClaimDurability.NoReplica);
            }

            try
            {
                var acknowledged = (long)await server.ExecuteAsync("WAIT", 1, (long)BarrierTimeout.TotalMilliseconds).ConfigureAwait(false);
                if (acknowledged >= 1)
                {
                    return new ClaimResult(code, ClaimDurability.Confirmed);
                }
            }
            catch (RedisException)
            {
                // Not confirmed; the step runs again below.
            }

            if (attempt >= BarrierRetries)
            {
                return new ClaimResult(code, ClaimDurability.NotConfirmed);
            }

            var repeat = await step().ConfigureAwait(false);
            if (repeat is not (ClaimCodes.Done or ClaimCodes.AlreadyDone))
            {
                return new ClaimResult(repeat, ClaimDurability.NotApplicable);
            }
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
            var record = ToPairs(await _db.HashGetAllAsync(Key, Master).ConfigureAwait(false));
            var decision = decide(record);
            if (decision.Writes.Count == 0)
            {
                return decision.Code;
            }

            // The whole record as read: its length and every field's value, so a change anywhere in it aborts the writes.
            var transaction = _db.CreateTransaction();
            transaction.AddCondition(Condition.HashLengthEqual(Key, record.Count));
            foreach (var (field, value) in record)
            {
                transaction.AddCondition(Condition.HashEqual(Key, field, value));
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
