using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    // The count is read back as a string: a Lua number holds 53 bits and a version needs 62.
    private const string HSetCScript = """
        if redis.call('HGET', KEYS[2], 'id') ~= ARGV[1] then return -1 end
        if #KEYS == 3 then redis.call('SET', KEYS[3], ARGV[7], 'PX', ARGV[8]) end
        if ARGV[2] == 'key' then redis.call('DEL', KEYS[1]) end
        for i = 9, #ARGV, 2 do redis.call('HSET', KEYS[1], ARGV[i], ARGV[i + 1]) end
        if ARGV[4] ~= '' then redis.call('HDEL', KEYS[1], ARGV[4]) end
        local r = 1
        if ARGV[5] ~= '' then
          redis.call('HSETNX', KEYS[1], ARGV[5], ARGV[6])
          local n = redis.pcall('HINCRBY', KEYS[1], ARGV[5], 1)
          if type(n) == 'table' and n.err then r = 0 else r = redis.call('HGET', KEYS[1], ARGV[5]) end
        end
        if ARGV[3] ~= '-1' then redis.call('PEXPIREAT', KEYS[1], ARGV[3]) elseif ARGV[2] == 'hash' then redis.call('PERSIST', KEYS[1]) end
        return r
        """;

    private const string HDelCScript = """
        if redis.call('HGET', KEYS[2], 'id') ~= ARGV[1] then return -1 end
        if #KEYS == 3 then redis.call('SET', KEYS[3], ARGV[4], 'PX', ARGV[5]) end
        if ARGV[2] ~= '' and redis.call('EXISTS', KEYS[1]) == 1 then
          redis.call('HSETNX', KEYS[1], ARGV[2], ARGV[3])
          redis.pcall('HINCRBY', KEYS[1], ARGV[2], 1)
        end
        for i = 6, #ARGV do redis.call('HDEL', KEYS[1], ARGV[i]) end
        if ARGV[2] ~= '' and redis.call('HLEN', KEYS[1]) == 1 and redis.call('HEXISTS', KEYS[1], ARGV[2]) == 1 then redis.call('HDEL', KEYS[1], ARGV[2]) end
        return 1
        """;

    private const string HExpCScript = """
        if redis.call('HGET', KEYS[2], 'id') ~= ARGV[1] then return -1 end
        if #KEYS == 3 then redis.call('SET', KEYS[3], ARGV[3], 'PX', ARGV[4]) end
        if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
        if ARGV[2] == '-1' then redis.call('PERSIST', KEYS[1]) else redis.call('PEXPIREAT', KEYS[1], ARGV[2]) end
        return 1
        """;

    /// <summary>HSETC: a hash write under the identity; on the Dst side it also replaces the fence.</summary>
    /// <param name="fence">The fence to replace on the Dst side; null on the Src side.</param>
    /// <returns>-1, the new version when counting (0 when the count could not move), otherwise 1.</returns>
    public async Task<long> HashWriteAsync(RedisKey key, RedisKey control, string id, ControlledHashWrite write, FenceBump? fence = null)
    {
        var args = new RedisValue[8 + (write.Entries.Count * 2)];
        args[0] = id;
        args[1] = write.Mode == HashCacheSetOption.KeyReplace ? "key" : "hash";
        args[2] = write.ExpireAtMilliseconds == NoExpiry ? "-1" : write.ExpireAtMilliseconds;
        args[3] = write.DropField ?? string.Empty;
        args[4] = write.VersionField ?? string.Empty;
        args[5] = write.VersionSeed;
        args[6] = fence?.Nonce ?? string.Empty;
        args[7] = fence?.TtlMilliseconds ?? 0;
        for (var i = 0; i < write.Entries.Count; i++)
        {
            args[8 + (i * 2)] = write.Entries[i].Name;
            args[9 + (i * 2)] = write.Entries[i].Value;
        }

        var reply = await EvaluateAsync(HSetCScript, Keys(key, control, fence), args).ConfigureAwait(false);
        if (reply is not null)
        {
            return reply.Resp2Type == ResultType.BulkString ? long.Parse(((string?)reply)!, CultureInfo.InvariantCulture) : (long)reply;
        }

        return await WatchedAsync(() => TryHashWriteAsync(key, control, id, write, fence)).ConfigureAwait(false);
    }

    /// <summary>HDELC: deletes fields under the identity; on the Dst side it also replaces the fence. A hash left with only its count is removed.</summary>
    /// <returns>-1 or 1.</returns>
    public async Task<long> HashDeleteFieldsAsync(RedisKey key, RedisKey control, string id, IReadOnlyList<RedisValue> fields, string? versionField = null, long versionSeed = 0, FenceBump? fence = null)
    {
        var args = new RedisValue[5 + fields.Count];
        args[0] = id;
        args[1] = versionField ?? string.Empty;
        args[2] = versionSeed;
        args[3] = fence?.Nonce ?? string.Empty;
        args[4] = fence?.TtlMilliseconds ?? 0;
        for (var i = 0; i < fields.Count; i++)
        {
            args[5 + i] = fields[i];
        }

        var reply = await EvaluateAsync(HDelCScript, Keys(key, control, fence), args).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        var result = await WatchedAsync(() => TryHashDeleteFieldsAsync(key, control, id, fields, versionField, versionSeed, fence)).ConfigureAwait(false);
        if (result == 1 && versionField is not null)
        {
            await RemoveCountOnlyHashAsync(key, control, id, versionField).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>HEXPC: sets the absolute expiry of a hash that exists, or removes it with -1, under the identity; on the Dst side it also replaces the fence.</summary>
    /// <returns>-1, 0 when the hash is absent, 1.</returns>
    public async Task<long> HashExpireAsync(RedisKey key, RedisKey control, string id, long expireAtMilliseconds, FenceBump? fence = null)
    {
        var reply = await EvaluateAsync(
            HExpCScript,
            Keys(key, control, fence),
            [id, expireAtMilliseconds == NoExpiry ? "-1" : expireAtMilliseconds, fence?.Nonce ?? string.Empty, fence?.TtlMilliseconds ?? 0]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            var exists = await _db.KeyExistsAsync(key, Master).ConfigureAwait(false);
            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.HashEqual(control, "id", id));
            transaction.AddCondition(exists ? Condition.KeyExists(key) : Condition.KeyNotExists(key));
            BumpIn(transaction, queued, fence);
            if (exists && expireAtMilliseconds == NoExpiry)
            {
                queued.Add(transaction.KeyPersistAsync(key, Master));
            }
            else if (exists)
            {
                queued.Add(transaction.KeyExpireAsync(key, At(expireAtMilliseconds), Master));
            }

            if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
            {
                return exists ? 1 : 0;
            }

            return await HoldsIdentityAsync(control, id).ConfigureAwait(false) ? null : IdentityMismatch;
        }).ConfigureAwait(false);
    }

    private static RedisKey[] Keys(RedisKey key, RedisKey control, FenceBump? fence) =>
        fence is { } f ? [key, control, f.Fence] : [key, control];

    private static void BumpIn(ITransaction transaction, List<Task> queued, FenceBump? fence)
    {
        if (fence is { } f)
        {
            queued.Add(transaction.StringSetAsync(f.Fence, f.Nonce, TimeSpan.FromMilliseconds(f.TtlMilliseconds), When.Always, Master));
        }
    }

    // The count stops at the 64-bit maximum while the write beside it lands; that write reads as uncounted.
    private static async Task<long> CountOrZeroAsync(Task<long> counted)
    {
        try
        {
            return await counted.ConfigureAwait(false);
        }
        catch (RedisServerException)
        {
            return 0;
        }
    }

    // The script removes a hash left with only its count in the same step; here a second transaction does, under the same WATCH-abort rule.
    private async Task<long?> TryHashWriteAsync(RedisKey key, RedisKey control, string id, ControlledHashWrite write, FenceBump? fence)
    {
        var transaction = _db.CreateTransaction();
        var queued = new List<Task>();
        transaction.AddCondition(Condition.HashEqual(control, "id", id));
        BumpIn(transaction, queued, fence);
        if (write.Mode == HashCacheSetOption.KeyReplace)
        {
            queued.Add(transaction.KeyDeleteAsync(key, Master));
        }

        if (write.Entries.Count > 0)
        {
            queued.Add(transaction.HashSetAsync(key, [.. write.Entries], Master));
        }
        if (write.DropField is { } drop)
        {
            queued.Add(transaction.HashDeleteAsync(key, drop, Master));
        }

        Task<long>? counted = null;
        if (write.VersionField is { } version)
        {
            queued.Add(transaction.HashSetAsync(key, version, write.VersionSeed, When.NotExists, Master));
            counted = transaction.HashIncrementAsync(key, version, 1, Master);
            counted.Forget();
        }

        if (write.ExpireAtMilliseconds != NoExpiry)
        {
            queued.Add(transaction.KeyExpireAsync(key, At(write.ExpireAtMilliseconds), Master));
        }
        else if (write.Mode == HashCacheSetOption.HashReplace)
        {
            queued.Add(transaction.KeyPersistAsync(key, Master));
        }

        if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
        {
            return counted is null ? 1 : await CountOrZeroAsync(counted).ConfigureAwait(false);
        }

        return await HoldsIdentityAsync(control, id).ConfigureAwait(false) ? null : IdentityMismatch;
    }

    private async Task<long?> TryHashDeleteFieldsAsync(RedisKey key, RedisKey control, string id, IReadOnlyList<RedisValue> fields, string? versionField, long versionSeed, FenceBump? fence)
    {
        var exists = versionField is not null && await _db.KeyExistsAsync(key, Master).ConfigureAwait(false);
        var transaction = _db.CreateTransaction();
        var queued = new List<Task>();
        transaction.AddCondition(Condition.HashEqual(control, "id", id));
        if (versionField is not null)
        {
            transaction.AddCondition(exists ? Condition.KeyExists(key) : Condition.KeyNotExists(key));
        }

        BumpIn(transaction, queued, fence);
        if (exists)
        {
            queued.Add(transaction.HashSetAsync(key, versionField, versionSeed, When.NotExists, Master));
            // At the 64-bit maximum the count stays and the delete still lands, as the script's pcall does; so this result is not awaited.
            transaction.HashIncrementAsync(key, versionField, 1, Master).Forget();
        }

        if (fields.Count > 0)
        {
            queued.Add(transaction.HashDeleteAsync(key, [.. fields], Master));
        }
        if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
        {
            return 1;
        }

        return await HoldsIdentityAsync(control, id).ConfigureAwait(false) ? null : IdentityMismatch;
    }

    private Task<long> RemoveCountOnlyHashAsync(RedisKey key, RedisKey control, string id, string versionField) =>
        WatchedAsync(async () =>
        {
            var emptied = _db.CreateTransaction();
            var queued = new List<Task>();
            emptied.AddCondition(Condition.HashEqual(control, "id", id));
            emptied.AddCondition(Condition.HashLengthEqual(key, 1));
            emptied.AddCondition(Condition.HashExists(key, versionField));
            queued.Add(emptied.HashDeleteAsync(key, versionField, Master));
            if (await ExecuteAllAsync(emptied, queued).ConfigureAwait(false))
            {
                return 1;
            }

            if (!await HoldsIdentityAsync(control, id).ConfigureAwait(false))
            {
                return IdentityMismatch;
            }

            var held = await _db.HashGetAllAsync(key, Master).ConfigureAwait(false);
            return held is [var only] && only.Name == versionField ? null : 0;
        });
}
