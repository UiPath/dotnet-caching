using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    // The one rule that decides whether a marker is current, shared by every script that reads a marker: the record's binding value is the field named by 'bf', or 'id' when it names none.
    private const string SmHelpers = """
        local function smbind(ctl)
          return redis.call('HGET', ctl, redis.call('HGET', ctl, 'bf') or 'id')
        end
        local function smlive(sm, b)
          return b and b ~= '' and string.sub(sm, 1, #b + 1) == b .. '|'
        end
        """;

    private const string SmgcOne = """
        local function smgc(ctl, field, expected)
          local v = redis.call('HGET', ctl, field)
          if v ~= expected then return 0 end
          local i = string.find(v, '|', 1, true)
          local b = i and string.sub(v, 1, i - 1) or v
          local cur = smbind(ctl)
          if (cur and b == cur) or b == redis.call('HGET', ctl, 'rg') then return -1 end
          return redis.call('HDEL', ctl, field)
        end
        """;

    private const string UnmarkScript = """
        if redis.call('HGET', KEYS[1], ARGV[1]) == ARGV[2] then return redis.call('HDEL', KEYS[1], ARGV[1]) end
        return 0
        """;

    private const string UnmarkManyScript = """
        local n = 0
        for i = 1, #ARGV, 2 do
          if redis.call('HGET', KEYS[1], ARGV[i]) == ARGV[i + 1] then n = n + redis.call('HDEL', KEYS[1], ARGV[i]) end
        end
        return n
        """;

    private const string SmgcScript = SmHelpers + "\n" + SmgcOne + "\nreturn smgc(KEYS[1], ARGV[1], ARGV[2])";

    private const string SmgcManyScript = SmHelpers + "\n" + SmgcOne + """

        local r = {}
        for i = 1, #ARGV, 2 do r[#r + 1] = smgc(KEYS[1], ARGV[i], ARGV[i + 1]) end
        return r
        """;

    // KEYS 1 L, 2 control record of L's slot. ARGV 1 id, 2 the marker's binding value. -9 when that value is neither the record's current binding nor its retiring one.
    private const string SrcDelGuardedScript = SmHelpers + """

        if redis.call('HGET', KEYS[2], 'id') ~= ARGV[1] then return -1 end
        local cur = smbind(KEYS[2])
        local rg = redis.call('HGET', KEYS[2], 'rg')
        if ARGV[2] == '' or (ARGV[2] ~= cur and ARGV[2] ~= rg) then return -9 end
        return redis.call('DEL', KEYS[1])
        """;

    private const string LimitSetScript = """
        if redis.call('HGET', KEYS[1], 'id') ~= ARGV[1] then return -1 end
        redis.call('HSET', KEYS[1], 'lim', ARGV[2])
        return 1
        """;

    // The record is read whole, deleted and written again in this one script, so no command sees it missing; pk starts again from the length.
    private const string CompactScript = """
        if redis.call('HGET', KEYS[1], 'id') ~= ARGV[1] then return -1 end
        if redis.call('HLEN', KEYS[1]) > 2048 then return -2 end
        local all = redis.call('HGETALL', KEYS[1])
        redis.call('DEL', KEYS[1])
        for i = 1, #all, 2 do redis.call('HSET', KEYS[1], all[i], all[i + 1]) end
        local n = redis.call('HLEN', KEYS[1])
        redis.call('HSET', KEYS[1], 'pk', n)
        return redis.call('HLEN', KEYS[1])
        """;

    /// <summary>UNMARK: removes the marker only while it holds <paramref name="expected"/>, whatever the identity: its caller deleted L first or saw it absent.</summary>
    public async Task<long> UnmarkAsync(RedisKey control, string field, string expected)
    {
        var reply = await EvaluateAsync(UnmarkScript, [control], [field, expected]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            if (await _db.HashGetAsync(control, field, Master).ConfigureAwait(false) != expected)
            {
                return 0;
            }

            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.HashEqual(control, field, expected));
            var removed = transaction.HashDeleteAsync(control, field, Master);
            queued.Add(removed);
            if (!await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
            {
                return null;
            }

            return await removed.ConfigureAwait(false) ? 1 : 0;
        }).ConfigureAwait(false);
    }

    public async Task<long> UnmarkManyAsync(RedisKey control, IReadOnlyList<(string Field, string Expected)> markers)
    {
        if (markers.Count == 0)
        {
            return 0;
        }

        var reply = await EvaluateAsync(UnmarkManyScript, [control], [.. markers.SelectMany(m => new RedisValue[] { m.Field, m.Expected })]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        long removed = 0;
        foreach (var (field, expected) in markers)
        {
            removed += await UnmarkAsync(control, field, expected).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>SMGC: removes an obsolete marker, one bound to neither the record's current binding value nor its retiring one.</summary>
    /// <returns>1 removed, 0 the marker is not there or changed, -1 the marker is live or retiring and stays.</returns>
    public async Task<long> SupersessionGcAsync(RedisKey control, string field, string expected)
    {
        var reply = await EvaluateAsync(SmgcScript, [control], [field, expected]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        // A changed identity or marker aborts the transaction; the next pass reads everything again. A change of the binding or retiring value comes with a change of the identity.
        return await WatchedAsync(() => TrySupersessionGcAsync(control, field, expected)).ConfigureAwait(false);
    }

    public async Task<long[]> SupersessionGcManyAsync(RedisKey control, IReadOnlyList<(string Field, string Expected)> markers)
    {
        if (markers.Count == 0)
        {
            return [];
        }

        var reply = await EvaluateAsync(SmgcManyScript, [control], [.. markers.SelectMany(m => new RedisValue[] { m.Field, m.Expected })]).ConfigureAwait(false);
        if (reply is not null)
        {
            return [.. ((RedisResult[]?)reply)!.Select(r => (long)r)];
        }

        var codes = new long[markers.Count];
        for (var i = 0; i < codes.Length; i++)
        {
            codes[i] = await SupersessionGcAsync(control, markers[i].Field, markers[i].Expected).ConfigureAwait(false);
        }

        return codes;
    }

    /// <summary>SRCDELG: the deletion of L whose authority is a marker; the marker's binding value must be the record's current one or its retiring one.</summary>
    /// <returns>-1 identity, -9 the binding ended, otherwise the delete count.</returns>
    public async Task<long> SourceDeleteGuardedAsync(RedisKey key, RedisKey control, string id, string binding)
    {
        var reply = await EvaluateAsync(SrcDelGuardedScript, [key, control], [id, binding]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(() => TrySourceDeleteGuardedAsync(key, control, id, binding)).ConfigureAwait(false);
    }

    public async Task<long> LimitSetAsync(RedisKey control, string id, long limit)
    {
        var reply = await EvaluateAsync(LimitSetScript, [control], [id, limit]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.HashEqual(control, MarkerBinding.IdentityField, id));
            queued.Add(transaction.HashSetAsync(control, "lim", limit, When.Always, Master));
            if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
            {
                return 1;
            }

            return await HoldsIdentityAsync(control, id).ConfigureAwait(false) ? null : IdentityMismatch;
        }).ConfigureAwait(false);
    }

    /// <returns>-1 identity, -2 for a record above 2,048 fields, otherwise its new length.</returns>
    public async Task<long> CompactControlAsync(RedisKey control, string id)
    {
        var reply = await EvaluateAsync(CompactScript, [control], [id]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        // Every field that was read is a condition of the transaction, which watches the record before it compares them: a field changed since the read, a marker replaced by another of the same length included, aborts it.
        return await WatchedAsync(async () =>
        {
            if (!await HoldsIdentityAsync(control, id).ConfigureAwait(false))
            {
                return IdentityMismatch;
            }

            var held = await _db.HashGetAllAsync(control, Master).ConfigureAwait(false);
            if (held.Length > 2048)
            {
                return CompactRefused;
            }

            var entries = held.Where(e => e.Name != "pk").Append(new HashEntry("pk", held.Length)).ToArray();
            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.HashLengthEqual(control, held.Length));
            foreach (var entry in held)
            {
                transaction.AddCondition(Condition.HashEqual(control, entry.Name, entry.Value));
            }

            queued.Add(transaction.KeyDeleteAsync(control, Master));
            queued.Add(transaction.HashSetAsync(control, entries, Master));
            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? entries.Length : null;
        }).ConfigureAwait(false);
    }

    private async Task<long?> TrySupersessionGcAsync(RedisKey control, string field, string expected)
    {
        if (await _db.HashGetAsync(control, field, Master).ConfigureAwait(false) != expected)
        {
            return 0;
        }

        var id = await _db.HashGetAsync(control, MarkerBinding.IdentityField, Master).ConfigureAwait(false);
        var current = await CurrentBindingAsync(control).ConfigureAwait(false);
        var retiring = await _db.HashGetAsync(control, MarkerBinding.RetiringField, Master).ConfigureAwait(false);
        if (MarkerBinding.IsLive(expected, current.IsNull ? null : current.ToString(), retiring.IsNull ? null : retiring.ToString()))
        {
            return -1;
        }

        var transaction = _db.CreateTransaction();
        var queued = new List<Task>();
        transaction.AddCondition(Condition.HashEqual(control, field, expected));
        transaction.AddCondition(id.IsNull ? Condition.HashNotExists(control, MarkerBinding.IdentityField) : Condition.HashEqual(control, MarkerBinding.IdentityField, id));
        var removed = transaction.HashDeleteAsync(control, field, Master);
        queued.Add(removed);
        if (!await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
        {
            return null;
        }

        return await removed.ConfigureAwait(false) ? 1 : 0;
    }

    private async Task<long?> TrySourceDeleteGuardedAsync(RedisKey key, RedisKey control, string id, string binding)
    {
        if (!await HoldsIdentityAsync(control, id).ConfigureAwait(false))
        {
            return IdentityMismatch;
        }

        var field = await _db.HashGetAsync(control, MarkerBinding.BindingFieldName, Master).ConfigureAwait(false);
        var bindingField = field.IsNull ? MarkerBinding.IdentityField : field.ToString();
        var current = await _db.HashGetAsync(control, bindingField, Master).ConfigureAwait(false);
        var retiring = await _db.HashGetAsync(control, MarkerBinding.RetiringField, Master).ConfigureAwait(false);
        var viaCurrent = binding.Length > 0 && current == binding;
        if (!viaCurrent && !(binding.Length > 0 && retiring == binding))
        {
            return BindingEnded;
        }

        var transaction = _db.CreateTransaction();
        var queued = new List<Task>();
        transaction.AddCondition(Condition.HashEqual(control, MarkerBinding.IdentityField, id));
        transaction.AddCondition(Condition.HashEqual(control, viaCurrent ? bindingField : MarkerBinding.RetiringField, binding));
        var deleted = transaction.KeyDeleteAsync(key, Master);
        if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
        {
            return await deleted.ConfigureAwait(false) ? 1 : 0;
        }

        // Aborted: the identity or the binding moved, or an unrelated field of the record did. The next pass reads again and says which.
        return null;
    }

    private async Task<RedisValue> CurrentBindingAsync(RedisKey control)
    {
        var field = await _db.HashGetAsync(control, MarkerBinding.BindingFieldName, Master).ConfigureAwait(false);
        return await _db.HashGetAsync(control, field.IsNull ? MarkerBinding.IdentityField : field.ToString(), Master).ConfigureAwait(false);
    }
}
