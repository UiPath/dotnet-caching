using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

internal sealed partial class MoveScripts
{
    private const string SrcDelScript = """
        if redis.call('HGET', KEYS[2], 'id') ~= ARGV[1] then return -1 end
        return redis.call('DEL', KEYS[1])
        """;

    private const string SrcSetScript = """
        if redis.call('HGET', KEYS[2], 'id') ~= ARGV[1] then return -1 end
        if ARGV[4] == 'nx' and redis.call('EXISTS', KEYS[1]) == 1 then return 2 end
        if ARGV[3] == '-1' then redis.call('SET', KEYS[1], ARGV[2]) else redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3]) end
        return 1
        """;

    private const string SrcExpScript = """
        if redis.call('HGET', KEYS[2], 'id') ~= ARGV[1] then return -1 end
        if ARGV[2] == '-1' then
          if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
          redis.call('PERSIST', KEYS[1])
          return 1
        end
        return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        """;

    private const string CmpDelScript = """
        if redis.call('HGET', KEYS[2], 'id') ~= ARGV[2] then return -1 end
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    private const string CmpDelHashScript = """
        if redis.call('HGET', KEYS[2], 'id') ~= ARGV[1] then return -1 end
        if redis.call('HLEN', KEYS[1]) ~= tonumber(ARGV[2]) then return 0 end
        for i = 3, #ARGV, 2 do
          if redis.call('HGET', KEYS[1], ARGV[i]) ~= ARGV[i + 1] then return 0 end
        end
        return redis.call('DEL', KEYS[1])
        """;

    /// <summary>SRCDEL: deletes a source key under the identity.</summary>
    /// <returns>-1 or the delete count.</returns>
    public async Task<long> SourceDeleteAsync(RedisKey key, RedisKey control, string id)
    {
        var reply = await EvaluateAsync(SrcDelScript, [key, control], [id]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.HashEqual(control, "id", id));
            var deleted = transaction.KeyDeleteAsync(key, Master);
            if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
            {
                return await deleted.ConfigureAwait(false) ? 1 : 0;
            }

            return await HoldsIdentityAsync(control, id).ConfigureAwait(false) ? null : IdentityMismatch;
        }).ConfigureAwait(false);
    }

    /// <summary>SRCSET: writes a source key under the identity; with <paramref name="onlyIfAbsent"/> an existing key is left alone.</summary>
    /// <returns>-1, 2 when <paramref name="onlyIfAbsent"/> found the key, 1.</returns>
    public async Task<long> SourceSetAsync(RedisKey key, RedisKey control, string id, RedisValue value, long px, bool onlyIfAbsent)
    {
        CheckLife(px, nameof(px));
        var reply = await EvaluateAsync(SrcSetScript, [key, control], [id, value, Ttl(px), onlyIfAbsent ? "nx" : "set"]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.HashEqual(control, "id", id));
            if (onlyIfAbsent)
            {
                transaction.AddCondition(Condition.KeyNotExists(key));
            }

            queued.Add(transaction.StringSetAsync(key, value, px == NoExpiry ? null : TimeSpan.FromMilliseconds(px), When.Always, Master));
            if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
            {
                return 1;
            }

            if (!await HoldsIdentityAsync(control, id).ConfigureAwait(false))
            {
                return IdentityMismatch;
            }

            return onlyIfAbsent && await _db.KeyExistsAsync(key, Master).ConfigureAwait(false) ? DestinationExists : null;
        }).ConfigureAwait(false);
    }

    /// <summary>SRCEXP: refreshes the life of a key that exists, on whichever side holds it; -1 removes the expiry.</summary>
    /// <returns>-1, 0 when the key is absent, 1 (also for a live key that already had no expiry).</returns>
    public async Task<long> SourceExpireAsync(RedisKey key, RedisKey control, string id, long px)
    {
        CheckLife(px, nameof(px));
        var reply = await EvaluateAsync(SrcExpScript, [key, control], [id, Ttl(px)]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.HashEqual(control, "id", id));
            Task<bool> changed;
            if (px == NoExpiry)
            {
                transaction.AddCondition(Condition.KeyExists(key));
                changed = transaction.KeyPersistAsync(key, Master);
            }
            else
            {
                changed = transaction.KeyExpireAsync(key, TimeSpan.FromMilliseconds(px), Master);
            }

            if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
            {
                return px == NoExpiry || await changed.ConfigureAwait(false) ? 1 : 0;
            }

            if (!await HoldsIdentityAsync(control, id).ConfigureAwait(false))
            {
                return IdentityMismatch;
            }

            return await _db.KeyExistsAsync(key, Master).ConfigureAwait(false) ? null : 0;
        }).ConfigureAwait(false);
    }

    /// <summary>CMPDEL: deletes the source only while it holds exactly <paramref name="expected"/>; a key of another type reads as changed.</summary>
    /// <returns>-1, 0 when the key changed or is gone, 1.</returns>
    public async Task<long> CompareDeleteAsync(RedisKey key, RedisKey control, string id, RedisValue expected)
    {
        try
        {
            var reply = await EvaluateAsync(CmpDelScript, [key, control], [expected, id]).ConfigureAwait(false);
            if (reply is not null)
            {
                return (long)reply;
            }

            return await WatchedAsync(async () =>
            {
                var transaction = _db.CreateTransaction();
                var queued = new List<Task>();
                transaction.AddCondition(Condition.HashEqual(control, "id", id));
                transaction.AddCondition(Condition.StringEqual(key, expected));
                var deleted = transaction.KeyDeleteAsync(key, Master);
                if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
                {
                    return await deleted.ConfigureAwait(false) ? 1 : 0;
                }

                if (!await HoldsIdentityAsync(control, id).ConfigureAwait(false))
                {
                    return IdentityMismatch;
                }

                return await _db.StringGetAsync(key, Master).ConfigureAwait(false) == expected ? null : 0;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsWrongType(ex))
        {
            // The script checks the identity before it touches the key; so does this.
            return await HoldsIdentityAsync(control, id).ConfigureAwait(false) ? 0 : IdentityMismatch;
        }
    }

    /// <summary>CMPDELH: deletes the source hash only while it holds exactly these fields and values; a key of another type reads as changed.</summary>
    /// <returns>-1, 0 when the hash changed or is gone, 1.</returns>
    public async Task<long> CompareDeleteHashAsync(RedisKey key, RedisKey control, string id, IReadOnlyList<HashEntry> expected)
    {
        var args = new RedisValue[2 + (expected.Count * 2)];
        args[0] = id;
        args[1] = expected.Count;
        for (var i = 0; i < expected.Count; i++)
        {
            args[2 + (i * 2)] = expected[i].Name;
            args[3 + (i * 2)] = expected[i].Value;
        }

        try
        {
            var reply = await EvaluateAsync(CmpDelHashScript, [key, control], args).ConfigureAwait(false);
            if (reply is not null)
            {
                return (long)reply;
            }

            return await WatchedAsync(async () =>
            {
                var transaction = _db.CreateTransaction();
                var queued = new List<Task>();
                transaction.AddCondition(Condition.HashEqual(control, "id", id));
                transaction.AddCondition(Condition.HashLengthEqual(key, expected.Count));
                foreach (var entry in expected)
                {
                    transaction.AddCondition(Condition.HashEqual(key, entry.Name, entry.Value));
                }

                var deleted = transaction.KeyDeleteAsync(key, Master);
                if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
                {
                    return await deleted.ConfigureAwait(false) ? 1 : 0;
                }

                if (!await HoldsIdentityAsync(control, id).ConfigureAwait(false))
                {
                    return IdentityMismatch;
                }

                return await HashHoldsAsync(key, expected).ConfigureAwait(false) ? null : 0;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsWrongType(ex))
        {
            // The script checks the identity before it touches the key; so does this.
            return await HoldsIdentityAsync(control, id).ConfigureAwait(false) ? 0 : IdentityMismatch;
        }
    }

    private async Task<bool> HashHoldsAsync(RedisKey key, IReadOnlyList<HashEntry> expected)
    {
        var held = await _db.HashGetAllAsync(key, Master).ConfigureAwait(false);
        if (held.Length != expected.Count)
        {
            return false;
        }

        var byName = held.ToDictionary(e => e.Name, e => e.Value);
        return expected.All(e => byName.TryGetValue(e.Name, out var v) && v == e.Value);
    }
}
