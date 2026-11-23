using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    public const long Written = 1;
    public const long AlreadyWrittenOrOlder = 0;
    public const long ExpectationFailed = 0;

    private const string CtlSyncScript = """
        local cur = redis.call('HGET', KEYS[1], 'ep')
        if cur then
          local c = tonumber(cur)
          local e = tonumber(ARGV[1])
          if e < c then return 0 end
          if e == c then
            if redis.call('HGET', KEYS[1], 'id') == ARGV[2] then
              redis.call('HSET', KEYS[1], 'id', ARGV[2])
              return 0
            end
            return -5
          end
        end
        redis.call('HSET', KEYS[1], 'ep', ARGV[1], 'id', ARGV[2], 'st', ARGV[3], 'dir', ARGV[4], 'np', ARGV[5], 'dur', '0',
          'pst', ARGV[6], 'pdir', ARGV[7], 'pnp', ARGV[8], 'pep', ARGV[9], 'pid', ARGV[10])
        for _, f in ipairs(redis.call('HKEYS', KEYS[1])) do
          if string.sub(f, 1, 2) == 'c:' then redis.call('HDEL', KEYS[1], f) end
        end
        for i = 11, #ARGV, 2 do redis.call('HSET', KEYS[1], 'c:' .. ARGV[i], 'OPEN:' .. ARGV[i + 1]) end
        return 1
        """;

    private const string CtlCommitScript = """
        local cur = redis.call('HGET', KEYS[1], 'ep')
        if cur and tonumber(cur) == tonumber(ARGV[1]) then
          if redis.call('HGET', KEYS[1], 'id') ~= ARGV[2] then return -5 end
          redis.call('HSET', KEYS[1], 'dur', '1')
          return 1
        end
        return 0
        """;

    private const string GateScript = """
        if redis.call('HGET', KEYS[1], 'id') ~= ARGV[1] then return -1 end
        local c = redis.call('HGET', KEYS[1], 'c:' .. ARGV[2])
        if c == ARGV[3] then redis.call('HSET', KEYS[1], 'c:' .. ARGV[2], ARGV[3]) return 1 end
        if ARGV[4] ~= '' and c ~= ARGV[4] then return 0 end
        redis.call('HSET', KEYS[1], 'c:' .. ARGV[2], ARGV[3])
        return 1
        """;

    /// <summary>CTLSYNC: the monotone write of a transition into one control record, creating it when absent; the gates are replaced as a set.</summary>
    /// <returns>1 written; 0 for an older <c>ep</c> or a repeat of this one (which rewrites the id, so a following WAIT has an offset to confirm); -5 for the same <c>ep</c> under another id.</returns>
    public async Task<long> ControlSyncAsync(RedisKey control, ControlRecord record) =>
        await ControlSyncWrite(control, record).RunAsync().ConfigureAwait(false);

    /// <summary>CTLSYNC as a write that runs alone or inside the transaction of a barrier; see <see cref="ControlSyncAsync"/> for its codes.</summary>
    public Write ControlSyncWrite(RedisKey control, ControlRecord record)
    {
        foreach (var (cid, _) in record.Gates)
        {
            MoveKeyNames.ValidateName(cid, nameof(record));
        }

        var args = new List<RedisValue>(10 + (record.Gates.Count * 2))
        {
            record.Ep, record.Id, record.State, record.Direction, record.NPop,
            record.PreviousState, record.PreviousDirection, record.PreviousNPop, record.PreviousEp, record.PreviousId,
        };
        foreach (var (cid, nonce) in record.Gates)
        {
            args.Add(cid);
            args.Add(nonce);
        }

        return new Write(this, CtlSyncScript, [control], [.. args], () => PlanControlSyncAsync(control, record));
    }

    /// <summary>CTLCOMMIT: marks the record of this transition committed; a repeat writes the flag again, so a following WAIT has an offset to confirm.</summary>
    /// <returns>1 committed, 0 for another <c>ep</c>, -5 for the same <c>ep</c> under another id; the controller treats 0 and -5 as failures.</returns>
    public async Task<long> ControlCommitAsync(RedisKey control, long ep, string id) =>
        await ControlCommitWrite(control, ep, id).RunAsync().ConfigureAwait(false);

    /// <summary>CTLCOMMIT as a write that runs alone or inside the transaction of a barrier; see <see cref="ControlCommitAsync"/> for its codes.</summary>
    public Write ControlCommitWrite(RedisKey control, long ep, string id) =>
        new(this, CtlCommitScript, [control], [ep, id], async () =>
        {
            var held = await _db.HashGetAsync(control, ["ep", "id"], Master).ConfigureAwait(false);
            if (held[0].IsNull || long.Parse(((string?)held[0])!, CultureInfo.InvariantCulture) != ep)
            {
                return new FallbackPlan(AlreadyWrittenOrOlder, null);
            }

            if (held[1] != id)
            {
                return new FallbackPlan(IdentityConflict, null);
            }

            return new FallbackPlan(Written, (transaction, queued) =>
            {
                AddIdentityConditions(transaction, control, held[0], held[1]);
                queued.Add(transaction.HashSetAsync(control, "dur", "1", When.Always, Master));
            });
        });

    /// <summary>GATE: sets the gate of a cache under the identity, for Clear; with <paramref name="expect"/> only while the gate still holds it.</summary>
    /// <param name="expect">The value the gate must hold, or empty for any; a delayed reopen of an old Clear then cannot reopen a newer gate.</param>
    /// <returns>-1 for another identity, 0 when the expectation failed, 1 set (also for a repeat, which writes the value again).</returns>
    public async Task<long> GateAsync(RedisKey control, string id, string cid, string value, string expect = "")
    {
        MoveKeyNames.ValidateName(cid, nameof(cid));
        var reply = await EvaluateAsync(GateScript, [control], [id, cid, value, expect]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        var field = "c:" + cid;
        return await WatchedAsync(async () =>
        {
            var held = await _db.HashGetAsync(control, ["id", field], Master).ConfigureAwait(false);
            if (held[0] != id)
            {
                return IdentityMismatch;
            }

            if (held[1] != value && expect.Length > 0 && held[1] != expect)
            {
                return ExpectationFailed;
            }

            var transaction = _db.CreateTransaction();

            var queued = new List<Task>();
            transaction.AddCondition(Condition.HashEqual(control, "id", id));
            transaction.AddCondition(held[1].IsNull ? Condition.HashNotExists(control, field) : Condition.HashEqual(control, field, held[1]));
            queued.Add(transaction.HashSetAsync(control, field, value, When.Always, Master));
            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? Written : null;
        }).ConfigureAwait(false);
    }

    private static void AddIdentityConditions(ITransaction transaction, RedisKey control, RedisValue ep, RedisValue id)
    {
        transaction.AddCondition(ep.IsNull ? Condition.HashNotExists(control, "ep") : Condition.HashEqual(control, "ep", ep));
        transaction.AddCondition(id.IsNull ? Condition.HashNotExists(control, "id") : Condition.HashEqual(control, "id", id));
    }

    private static void QueueControlSync(ITransaction transaction, RedisKey control, ControlRecord record, RedisValue ep, RedisValue heldId, int heldLength, Dictionary<string, RedisValue> fields, List<Task> queued)
    {
        AddIdentityConditions(transaction, control, ep, heldId);

        // A new gate changes the hash length and aborts this transaction rather than escaping deletion.
        transaction.AddCondition(Condition.HashLengthEqual(control, heldLength));
        queued.Add(transaction.HashSetAsync(
            control,
            [
                new("ep", record.Ep), new("id", record.Id), new("st", record.State), new("dir", record.Direction), new("np", record.NPop), new("dur", "0"),
                new("pst", record.PreviousState), new("pdir", record.PreviousDirection), new("pnp", record.PreviousNPop), new("pep", record.PreviousEp), new("pid", record.PreviousId),
            ],
            Master));
        var gates = fields.Keys.Where(f => f.StartsWith("c:", StringComparison.Ordinal)).Select(f => (RedisValue)f).ToArray();
        if (gates.Length > 0)
        {
            queued.Add(transaction.HashDeleteAsync(control, gates, Master));
        }

        if (record.Gates.Count > 0)
        {
            queued.Add(transaction.HashSetAsync(control, [.. record.Gates.Select(g => new HashEntry("c:" + g.Cid, "OPEN:" + g.Nonce))], Master));
        }
    }

    private async Task<FallbackPlan> PlanControlSyncAsync(RedisKey control, ControlRecord record)
    {
        var held = await _db.HashGetAllAsync(control, Master).ConfigureAwait(false);
        var fields = held.ToDictionary(e => e.Name.ToString(), e => e.Value);
        fields.TryGetValue("ep", out var ep);
        fields.TryGetValue("id", out var heldId);
        if (!ep.IsNull)
        {
            var current = long.Parse(ep.ToString(), CultureInfo.InvariantCulture);
            if (record.Ep < current)
            {
                return new FallbackPlan(AlreadyWrittenOrOlder, null);
            }

            if (record.Ep == current)
            {
                if (heldId != record.Id)
                {
                    return new FallbackPlan(IdentityConflict, null);
                }

                return new FallbackPlan(AlreadyWrittenOrOlder, (transaction, queued) =>
                {
                    AddIdentityConditions(transaction, control, ep, heldId);
                    queued.Add(transaction.HashSetAsync(control, "id", record.Id, When.Always, Master));
                });
            }
        }

        return new FallbackPlan(Written, (transaction, queued) => QueueControlSync(transaction, control, record, ep, heldId, held.Length, fields, queued));
    }
}
