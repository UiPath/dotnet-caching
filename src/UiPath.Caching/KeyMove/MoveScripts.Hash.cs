using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    // The count is read back as a string: a Lua number holds 53 bits and a version needs 62.
    // ARGV 1 id, 2 mode, 3 expiry, 4 drop field, 5 version field, 6 seed, 7 fence nonce, 8 fence life, 9 '1' for a fence-conditional retry, 10 the observed token, 11 marker field (setm), 12 first, 13 dl (as BUMP setm), 14.. field and value pairs.
    // -6 under a retry whose fence holds neither token; with setm, -6 -7 -8 as BUMP setm. Each changes nothing. A setm reply is {result, HLEN, marker}.
    private const string HSetCScript = SmHelpers + """

        if redis.call('HGET', KEYS[2], 'id') ~= ARGV[1] then return -1 end
        if ARGV[9] == '1' then
          local g = redis.call('GET', KEYS[3])
          if (not g) or (g ~= ARGV[7] and g ~= ARGV[10]) then return -6 end
        end
        local b
        if ARGV[11] ~= '' then
          local t = redis.call('TIME')
          if ARGV[13] ~= '0' and t[1] * 1000 + math.floor(t[2] / 1000) > tonumber(ARGV[13]) then return -7 end
          b = smbind(KEYS[2])
          if (not b) or b == '' then return -8 end
          if ARGV[12] == '1' and redis.call('HEXISTS', KEYS[2], ARGV[11]) == 0 and redis.call('HLEN', KEYS[2]) >= tonumber(redis.call('HGET', KEYS[2], 'lim') or '0') then return -6 end
        end
        if #KEYS == 3 then redis.call('SET', KEYS[3], ARGV[7], 'PX', ARGV[8]) end
        if ARGV[2] == 'key' then redis.call('DEL', KEYS[1]) end
        for i = 14, #ARGV, 2 do redis.call('HSET', KEYS[1], ARGV[i], ARGV[i + 1]) end
        if ARGV[4] ~= '' then redis.call('HDEL', KEYS[1], ARGV[4]) end
        local r = 1
        if ARGV[5] ~= '' then
          redis.call('HSETNX', KEYS[1], ARGV[5], ARGV[6])
          local n = redis.pcall('HINCRBY', KEYS[1], ARGV[5], 1)
          if type(n) == 'table' and n.err then r = 0 else r = redis.call('HGET', KEYS[1], ARGV[5]) end
        end
        if ARGV[3] ~= '-1' then redis.call('PEXPIREAT', KEYS[1], ARGV[3]) elseif ARGV[2] == 'hash' then redis.call('PERSIST', KEYS[1]) end
        if ARGV[11] ~= '' then
          local mk = b .. '|' .. ARGV[7]
          redis.call('HSET', KEYS[2], ARGV[11], mk)
          local n = redis.call('HLEN', KEYS[2])
          if n > tonumber(redis.call('HGET', KEYS[2], 'pk') or '0') then redis.call('HSET', KEYS[2], 'pk', n) end
          return {r, n, mk}
        end
        return r
        """;

    // ARGV 1 id, 2 version field, 3 seed, 4 fence nonce, 5 fence life, 6 '1' for a fence-conditional retry, 7 the observed token, 8.. fields. -6 under a retry whose fence holds neither token.
    private const string HDelCScript = """
        if redis.call('HGET', KEYS[2], 'id') ~= ARGV[1] then return -1 end
        if ARGV[6] == '1' then
          local g = redis.call('GET', KEYS[3])
          if (not g) or (g ~= ARGV[4] and g ~= ARGV[7]) then return -6 end
        end
        if #KEYS == 3 then redis.call('SET', KEYS[3], ARGV[4], 'PX', ARGV[5]) end
        if ARGV[2] ~= '' and redis.call('EXISTS', KEYS[1]) == 1 then
          redis.call('HSETNX', KEYS[1], ARGV[2], ARGV[3])
          redis.pcall('HINCRBY', KEYS[1], ARGV[2], 1)
        end
        for i = 8, #ARGV do redis.call('HDEL', KEYS[1], ARGV[i]) end
        if ARGV[2] ~= '' and redis.call('HLEN', KEYS[1]) == 1 and redis.call('HEXISTS', KEYS[1], ARGV[2]) == 1 then redis.call('HDEL', KEYS[1], ARGV[2]) end
        return 1
        """;

    private const string HExpCScript = """
        if redis.call('HGET', KEYS[2], 'id') ~= ARGV[1] then return -1 end
        if #KEYS == 3 then redis.call('SET', KEYS[3], ARGV[3], 'PX', ARGV[4]) end
        if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
        -- The metadata first, as the library's refresh does: an expiry that has passed deletes the key, and a field written after it would make a key of its own that never expires.
        if ARGV[5] == 'del' then redis.call('HDEL', KEYS[1], ARGV[6]) elseif ARGV[5] == 'mark' then redis.call('HSET', KEYS[1], ARGV[6], '') end
        if ARGV[2] == '-1' then redis.call('PERSIST', KEYS[1]) else redis.call('PEXPIREAT', KEYS[1], ARGV[2]) end
        return 1
        """;

    /// <summary>HSETC: a hash write under the identity; on the Dst side it also replaces the fence.</summary>
    /// <param name="fence">The fence to replace on the Dst side; null on the Src side.</param>
    /// <param name="retry">Makes it the retry form: the write lands only while the fence holds the nonce of <paramref name="fence"/> or the observed token. Needs <paramref name="fence"/>.</param>
    /// <returns>-1, <see cref="Superseded"/> for a retry the fence refuses, the new version when counting (0 when the count could not move), otherwise 1.</returns>
    public async Task<long> HashWriteAsync(RedisKey key, RedisKey control, string id, ControlledHashWrite write, FenceBump? fence = null, FenceRetry? retry = null)
    {
        if (retry is not null && fence is null)
        {
            throw new ArgumentException("A retried hash write is conditional on the fence, so it needs one.", nameof(retry));
        }

        var reply = await HashWriteCoreAsync(key, control, id, write, fence, retry, marker: null).ConfigureAwait(false);
        return reply.Code == 1 ? reply.Result : reply.Code;
    }

    /// <summary>HSETC setm: the whole replace of a Dst-side hash of a cache that has no origin, with the supersession marker of the in-place key set in the same step, as BUMP setm does for a string.</summary>
    /// <param name="markerField">The field <c>sm:</c> and the in-place key's physical text.</param>
    /// <param name="first">True only for the first attempt of a logical write: it is refused when the record is at its <c>lim</c>. A rerun is never refused for capacity.</param>
    /// <param name="deadline">The absolute time on the server's clock in Unix milliseconds after which the script refuses the command with <see cref="Late"/>, or 0 for none. A server without scripts cannot enforce it.</param>
    public Task<MarkedHashWrite> HashWriteMarkedAsync(RedisKey key, RedisKey control, string id, ControlledHashWrite write, FenceBump fence, string markerField, bool first, long deadline)
    {
        if (write.Mode != HashCacheSetOption.KeyReplace)
        {
            throw new ArgumentException("Only a whole replace carries a marker: a merge first makes sure the in-place hash is gone.", nameof(write));
        }

        ArgumentException.ThrowIfNullOrEmpty(markerField);
        return HashWriteCoreAsync(key, control, id, write, fence, retry: null, new MarkerArguments(markerField, first, deadline));
    }

    /// <summary>HDELC: deletes fields under the identity; on the Dst side it also replaces the fence. A hash left with only its count is removed.</summary>
    /// <param name="retry">Makes it the retry form, as for <see cref="HashWriteAsync"/>. Needs <paramref name="fence"/>.</param>
    /// <returns>-1, <see cref="Superseded"/> for a retry the fence refuses, otherwise 1.</returns>
    public async Task<long> HashDeleteFieldsAsync(RedisKey key, RedisKey control, string id, IReadOnlyList<RedisValue> fields, string? versionField = null, long versionSeed = 0, FenceBump? fence = null, FenceRetry? retry = null)
    {
        if (retry is not null && fence is null)
        {
            throw new ArgumentException("A retried hash delete is conditional on the fence, so it needs one.", nameof(retry));
        }

        var args = new RedisValue[7 + fields.Count];
        args[0] = id;
        args[1] = versionField ?? string.Empty;
        args[2] = versionSeed;
        args[3] = fence?.Nonce ?? string.Empty;
        args[4] = fence?.TtlMilliseconds ?? 0;
        args[5] = retry is null ? "0" : "1";
        args[6] = retry?.Observed ?? string.Empty;
        for (var i = 0; i < fields.Count; i++)
        {
            args[7 + i] = fields[i];
        }

        var reply = await EvaluateAsync(HDelCScript, Keys(key, control, fence), args).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        var result = await WatchedAsync(() => TryHashDeleteFieldsAsync(key, control, id, fields, versionField, versionSeed, fence, retry)).ConfigureAwait(false);
        if (result == 1 && versionField is not null)
        {
            await RemoveCountOnlyHashAsync(key, control, id, versionField).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>HEXPC: sets the absolute expiry of a hash that exists, or removes it with -1, under the identity, and with <paramref name="clear"/> clears its metadata in the same step; on the Dst side it also replaces the fence.</summary>
    /// <param name="clear">What the refresh does to the metadata field, as the library's options refresh does with no metadata: <see cref="HashMetadataClear.Remove"/> deletes it, and <see cref="HashMetadataClear.Mark"/> sets the empty marker where the library caches nulls.</param>
    /// <returns>-1, 0 when the hash is absent, 1.</returns>
    public async Task<long> HashExpireAsync(RedisKey key, RedisKey control, string id, long expireAtMilliseconds, FenceBump? fence = null, HashMetadataClear clear = HashMetadataClear.Keep)
    {
        var reply = await EvaluateAsync(
            HExpCScript,
            Keys(key, control, fence),
            [id, expireAtMilliseconds == NoExpiry ? "-1" : expireAtMilliseconds, fence?.Nonce ?? string.Empty, fence?.TtlMilliseconds ?? 0, clear switch { HashMetadataClear.Remove => "del", HashMetadataClear.Mark => "mark", _ => string.Empty }, KnownFieldNames.MetadataKey]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(() => TryHashExpireAsync(key, control, id, expireAtMilliseconds, fence, clear)).ConfigureAwait(false);
    }

    // The version count when counting, which no refusal code can be.
    private static MarkedHashWrite Replied(long code) => code < 0 ? new MarkedHashWrite(code, code) : new MarkedHashWrite(1, code);

    private static long CountOf(RedisResult reply) =>
        reply.Resp2Type == ResultType.BulkString ? long.Parse(((string?)reply)!, CultureInfo.InvariantCulture) : (long)reply;

    // The fields, the drop, the count and the expiry of one hash write, queued into a transaction; the task of the count, or null when it counts none.
    private static Task<long>? QueueHashWrite(ITransaction transaction, List<Task> queued, RedisKey key, ControlledHashWrite write)
    {
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

        return counted;
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

    private async Task<MarkedHashWrite> HashWriteCoreAsync(RedisKey key, RedisKey control, string id, ControlledHashWrite write, FenceBump? fence, FenceRetry? retry, MarkerArguments? marker)
    {
        var args = new RedisValue[13 + (write.Entries.Count * 2)];
        args[0] = id;
        args[1] = write.Mode == HashCacheSetOption.KeyReplace ? "key" : "hash";
        args[2] = write.ExpireAtMilliseconds == NoExpiry ? "-1" : write.ExpireAtMilliseconds;
        args[3] = write.DropField ?? string.Empty;
        args[4] = write.VersionField ?? string.Empty;
        args[5] = write.VersionSeed;
        args[6] = fence?.Nonce ?? string.Empty;
        args[7] = fence?.TtlMilliseconds ?? 0;
        args[8] = retry is null ? "0" : "1";
        args[9] = retry?.Observed ?? string.Empty;
        args[10] = marker?.MarkerField ?? string.Empty;
        args[11] = marker is { First: true } ? "1" : "0";
        args[12] = marker?.Deadline ?? 0;
        for (var i = 0; i < write.Entries.Count; i++)
        {
            args[13 + (i * 2)] = write.Entries[i].Name;
            args[14 + (i * 2)] = write.Entries[i].Value;
        }

        var reply = await EvaluateAsync(HSetCScript, Keys(key, control, fence), args).ConfigureAwait(false);
        if (reply is not null)
        {
            if (reply.Resp2Type == ResultType.Array)
            {
                var parts = ((RedisResult[]?)reply)!;
                return new MarkedHashWrite(1, CountOf(parts[0]), (long)parts[1], (string?)parts[2]);
            }

            return Replied(CountOf(reply));
        }

        return await WatchedOfAsync<MarkedHashWrite>(() => TryHashWriteAsync(key, control, id, write, fence, retry, marker)).ConfigureAwait(false);
    }

    private async Task<MarkedHashWrite?> TryHashWriteAsync(RedisKey key, RedisKey control, string id, ControlledHashWrite write, FenceBump? fence, FenceRetry? retry, MarkerArguments? marker)
    {
        var (refused, held, admission) = await JudgeHashWriteAsync(control, id, fence, retry, marker).ConfigureAwait(false);
        if (refused is { } refusal)
        {
            return refusal;
        }

        var transaction = _db.CreateTransaction();
        var queued = new List<Task>();
        transaction.AddCondition(Condition.HashEqual(control, "id", id));
        if (held is not null)
        {
            transaction.AddCondition(Condition.StringEqual(fence!.Value.Fence, held));
        }

        if (marker is { } m)
        {
            AddMarkerConditions(transaction, control, m.MarkerField, admission, m.First);
        }

        BumpIn(transaction, queued, fence);
        var counted = QueueHashWrite(transaction, queued, key, write);
        string? written = null;
        Task<long>? length = null;
        if (marker is { } placed)
        {
            written = $"{admission.Binding}|{fence!.Value.Nonce}";
            queued.Add(transaction.HashSetAsync(control, placed.MarkerField, written, When.Always, Master));
            length = transaction.HashLengthAsync(control, Master);
            queued.Add(length);
        }

        if (!await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
        {
            return await HoldsIdentityAsync(control, id).ConfigureAwait(false) ? null : Replied(IdentityMismatch);
        }

        var result = counted is null ? 1 : await CountOrZeroAsync(counted).ConfigureAwait(false);
        if (length is null)
        {
            return new MarkedHashWrite(1, result);
        }

        var count = await length.ConfigureAwait(false);
        await RaisePeakAsync(control, count).ConfigureAwait(false);
        return new MarkedHashWrite(1, result, count, written);
    }

    // The checks a hash write makes before its transaction, in the order of the script: the retry's fence, then the identity and the admission of the marker. A refusal, or the fence token held and the admission.
    private async Task<(MarkedHashWrite? Refused, string? Held, MarkerAdmission Admission)> JudgeHashWriteAsync(RedisKey control, string id, FenceBump? fence, FenceRetry? retry, MarkerArguments? marker)
    {
        string? held = null;
        if (retry is not null)
        {
            var (refusal, token) = await JudgeRetryAsync(control, id, fence!.Value, retry.Value).ConfigureAwait(false);
            if (refusal != 0)
            {
                return (Replied(refusal), null, default);
            }

            held = token;
        }

        var admission = default(MarkerAdmission);
        if (marker is { } wanted)
        {
            if (!await HoldsIdentityAsync(control, id).ConfigureAwait(false))
            {
                return (Replied(IdentityMismatch), null, default);
            }

            admission = await AdmitMarkerAsync(control, wanted.MarkerField, wanted.First).ConfigureAwait(false);
            if (admission.Refusal != 0)
            {
                return (Replied(admission.Refusal), null, default);
            }
        }

        return (null, held, admission);
    }

    // The identity and the fence in the order of the script; the refusal code, or 0 and the token that is held, which the transaction then requires.
    private async Task<(long Refusal, string? Held)> JudgeRetryAsync(RedisKey control, string id, FenceBump fence, FenceRetry retry)
    {
        if (!await HoldsIdentityAsync(control, id).ConfigureAwait(false))
        {
            return (IdentityMismatch, null);
        }

        string? held = await _db.StringGetAsync(fence.Fence, Master).ConfigureAwait(false);
        return held is null || (held != fence.Nonce && held != (retry.Observed ?? string.Empty)) ? (Superseded, null) : (0, held);
    }

    private async Task<long?> TryHashExpireAsync(RedisKey key, RedisKey control, string id, long expireAtMilliseconds, FenceBump? fence, HashMetadataClear clear)
    {
        var exists = await _db.KeyExistsAsync(key, Master).ConfigureAwait(false);
        var transaction = _db.CreateTransaction();
        var queued = new List<Task>();
        transaction.AddCondition(Condition.HashEqual(control, "id", id));
        transaction.AddCondition(exists ? Condition.KeyExists(key) : Condition.KeyNotExists(key));
        BumpIn(transaction, queued, fence);
        // The metadata first, as the library's refresh does: an expiry that has passed deletes the key, and a field written after it would make a key of its own that never expires.
        if (exists && clear == HashMetadataClear.Remove)
        {
            queued.Add(transaction.HashDeleteAsync(key, KnownFieldNames.MetadataKey, Master));
        }
        else if (exists && clear == HashMetadataClear.Mark)
        {
            queued.Add(transaction.HashSetAsync(key, KnownFieldNames.MetadataKey, RedisValue.EmptyString, When.Always, Master));
        }

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
    }

    private async Task<long?> TryHashDeleteFieldsAsync(RedisKey key, RedisKey control, string id, IReadOnlyList<RedisValue> fields, string? versionField, long versionSeed, FenceBump? fence, FenceRetry? retry)
    {
        string? held = null;
        if (retry is not null)
        {
            var (refusal, token) = await JudgeRetryAsync(control, id, fence!.Value, retry.Value).ConfigureAwait(false);
            if (refusal != 0)
            {
                return refusal;
            }

            held = token;
        }

        var exists = versionField is not null && await _db.KeyExistsAsync(key, Master).ConfigureAwait(false);
        var transaction = _db.CreateTransaction();
        var queued = new List<Task>();
        transaction.AddCondition(Condition.HashEqual(control, "id", id));
        if (held is not null)
        {
            transaction.AddCondition(Condition.StringEqual(fence!.Value.Fence, held));
        }

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

    // The script removes a hash left with only its count in the same step; here a second transaction does, under the same WATCH-abort rule.
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

    private readonly record struct MarkerArguments(string MarkerField, bool First, long Deadline);
}
