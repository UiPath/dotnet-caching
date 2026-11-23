using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    public const long FenceMoved = 0;
    public const long Published = 1;
    public const long DestinationExists = 2;
    public const long StagingMissing = 3;

    private const string CapScript = """
        if redis.call('HGET', KEYS[3], 'id') ~= ARGV[3] then return {-1} end
        local m = redis.call('HGET', KEYS[3], 'c:' .. ARGV[4])
        if (not m) or string.sub(m, 1, 6) == 'CLOSED' then return {-2} end
        local g = redis.call('GET', KEYS[1])
        if not g then g = ARGV[2]; redis.call('SET', KEYS[1], g) end
        redis.call('PEXPIRE', KEYS[1], ARGV[1])
        local t = redis.call('TIME')
        return {0, g, redis.call('EXISTS', KEYS[2]), m, string.format('%.0f', t[1] * 1000 + math.floor(t[2] / 1000))}
        """;

    private const string PubScript = """
        if redis.call('HGET', KEYS[3], 'id') ~= ARGV[6] then return -1 end
        local m = redis.call('HGET', KEYS[3], 'c:' .. ARGV[7])
        if (not m) or m ~= ARGV[5] or string.sub(m, 1, 6) == 'CLOSED' then return -2 end
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
        if ARGV[4] == 'nx' and redis.call('EXISTS', KEYS[2]) == 1 then return 2 end
        local deadline = nil
        if ARGV[3] ~= '-1' and ARGV[10] ~= '-1' then
          local t = redis.call('TIME')
          local now = t[1] * 1000 + math.floor(t[2] / 1000)
          deadline = tonumber(ARGV[10]) + tonumber(ARGV[3])
          if deadline - now < 1 then return -3 end
        end
        redis.call('SET', KEYS[2], ARGV[2])
        if deadline then redis.call('PEXPIREAT', KEYS[2], string.format('%.0f', deadline)) end
        if ARGV[10] == '-1' and ARGV[3] ~= '-1' then redis.call('PEXPIRE', KEYS[2], ARGV[3]) end
        redis.call('SET', KEYS[1], ARGV[8], 'PX', ARGV[9])
        return 1
        """;

    private const string PubHashScript = """
        if redis.call('EXISTS', KEYS[3]) == 0 then return 0 end
        if redis.call('HGET', KEYS[4], 'id') ~= ARGV[5] then redis.call('DEL', KEYS[3]) return -1 end
        local m = redis.call('HGET', KEYS[4], 'c:' .. ARGV[6])
        if (not m) or m ~= ARGV[4] or string.sub(m, 1, 6) == 'CLOSED' then redis.call('DEL', KEYS[3]) return -2 end
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then redis.call('DEL', KEYS[3]) return 0 end
        if ARGV[3] == 'nx' and redis.call('EXISTS', KEYS[2]) == 1 then redis.call('DEL', KEYS[3]) return 2 end
        local deadline = nil
        if ARGV[2] ~= '-1' then
          local t = redis.call('TIME')
          local now = t[1] * 1000 + math.floor(t[2] / 1000)
          deadline = tonumber(ARGV[9]) + tonumber(ARGV[2])
          if deadline - now < 1 then redis.call('DEL', KEYS[3]) return -3 end
        end
        redis.call('RENAME', KEYS[3], KEYS[2])
        if deadline then redis.call('PEXPIREAT', KEYS[2], string.format('%.0f', deadline)) else redis.call('PERSIST', KEYS[2]) end
        redis.call('SET', KEYS[1], ARGV[7], 'PX', ARGV[8])
        return 1
        """;

    private const string BumpScript = """
        if redis.call('HGET', KEYS[3], 'id') ~= ARGV[6] then return -1 end
        redis.call('SET', KEYS[1], ARGV[5], 'PX', ARGV[1])
        if ARGV[2] == 'none' then return 1 end
        if ARGV[2] == 'del' then return redis.call('DEL', KEYS[2]) end
        if ARGV[4] == '-1' then redis.call('SET', KEYS[2], ARGV[3]) else redis.call('SET', KEYS[2], ARGV[3], 'PX', ARGV[4]) end
        return 1
        """;

    private const string FillScript = """
        if redis.call('HGET', KEYS[3], 'id') ~= ARGV[5] then return -1 end
        if redis.call('EXISTS', KEYS[2]) == 1 then return 2 end
        if ARGV[2] == '-1' then redis.call('SET', KEYS[2], ARGV[1]) else redis.call('SET', KEYS[2], ARGV[1], 'PX', ARGV[2]) end
        redis.call('SET', KEYS[1], ARGV[3], 'PX', ARGV[4])
        return 1
        """;

    private const string FillHashScript = """
        if redis.call('HGET', KEYS[4], 'id') ~= ARGV[1] then return -1 end
        if redis.call('EXISTS', KEYS[1]) == 0 then return 3 end
        if redis.call('EXISTS', KEYS[2]) == 1 then return 2 end
        redis.call('RENAME', KEYS[1], KEYS[2])
        if ARGV[2] == '-1' then redis.call('PERSIST', KEYS[2]) else redis.call('PEXPIRE', KEYS[2], ARGV[2]) end
        redis.call('SET', KEYS[3], ARGV[3], 'PX', ARGV[4])
        return 1
        """;

    /// <summary>CAP: takes the fence token in force (creating it from <paramref name="nonce"/> when absent) and the capture time of the Dst server.</summary>
    public async Task<Capture> CaptureAsync(FencedKeys keys, string id, string cid, string nonce, long fenceTtlMilliseconds)
    {
        var reply = await EvaluateAsync(CapScript, [keys.Fence, keys.Data, keys.Control], [fenceTtlMilliseconds, nonce, id, cid]).ConfigureAwait(false);
        if (reply is not null)
        {
            var parts = ((RedisResult[]?)reply)!;
            var code = (long)parts[0];
            return code != 0
                ? new Capture(code, null, false, null, 0)
                : new Capture(0, (string?)parts[1], (long)parts[2] == 1, (string?)parts[3], long.Parse(((string?)parts[4])!, CultureInfo.InvariantCulture));
        }

        // A fence evicted or expired between its SET NX and its GET reads as no token, and the capture runs again.
        for (var attempt = 0; attempt < MaxWatchAborts; attempt++)
        {
            if (await CaptureInBatchAsync(keys, id, cid, nonce, fenceTtlMilliseconds).ConfigureAwait(false) is { } capture)
            {
                return capture;
            }
        }

        throw new RedisException($"The fence of '{keys.Data}' was gone at every one of {MaxWatchAborts} captures; the capture is refused.");
    }

    /// <summary>PUB: publishes <paramref name="value"/> under the fence and the gate and replaces the fence; never past the deadline <c>tcap + px</c> of the Dst server.</summary>
    /// <returns>-1 identity, -2 gate, 0 fence moved or gone, 2 destination exists, -3 past the deadline, 1 published.</returns>
    public async Task<long> PublishAsync(FencedKeys keys, string id, RedisValue value, Publication publication)
    {
        CheckLife(publication.Px, nameof(publication));
        var p = publication;
        var reply = await EvaluateAsync(
            PubScript,
            [keys.Fence, keys.Data, keys.Control],
            [p.Token, value, Ttl(p.Px), p.OnlyIfAbsent ? "nx" : "set", p.GateSeen, id, p.Cid, p.NewNonce, p.FenceTtlMilliseconds, Ttl(p.Tcap)]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        if (IsClosed(p.GateSeen))
        {
            return await HoldsIdentityAsync(keys.Control, id).ConfigureAwait(false) ? GateRefused : IdentityMismatch;
        }

        long? deadline = null;
        if (p.Px != NoExpiry && p.Tcap != NoExpiry)
        {
            deadline = p.Tcap + p.Px;
            if (deadline - await ServerTimeAsync(keys.Data).ConfigureAwait(false) < 1)
            {
                return PastDeadline;
            }
        }

        return await WatchedAsync(async () =>
        {
            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            AddPublishConditions(transaction, keys, id, p);
            queued.Add(transaction.StringSetAsync(keys.Data, value, null, When.Always, Master));
            if (deadline is { } at)
            {
                queued.Add(transaction.KeyExpireAsync(keys.Data, At(at), Master));
            }
            else if (p.Px != NoExpiry)
            {
                queued.Add(transaction.KeyExpireAsync(keys.Data, TimeSpan.FromMilliseconds(p.Px), Master));
            }

            queued.Add(transaction.StringSetAsync(keys.Fence, p.NewNonce, TimeSpan.FromMilliseconds(p.FenceTtlMilliseconds), When.Always, Master));
            if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
            {
                return Published;
            }

            return await PublishRefusalAsync(keys, id, p).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>PUBH: renames the staging hash over the destination under the fence and the gate, with the deadline rule of PUB.</summary>
    /// <returns>As <see cref="PublishAsync"/>; a missing staging key is 0, and every result but 1 deletes the staging key.</returns>
    public async Task<long> PublishHashAsync(FencedKeys keys, RedisKey staging, string id, Publication publication)
    {
        CheckLife(publication.Px, nameof(publication));
        var p = publication;
        if (p.Px != NoExpiry && p.Tcap == NoExpiry)
        {
            throw new ArgumentException("A hash publish with a life needs the capture time; a create goes through FILLH.", nameof(publication));
        }

        var reply = await EvaluateAsync(
            PubHashScript,
            [keys.Fence, keys.Data, staging, keys.Control],
            [p.Token, Ttl(p.Px), p.OnlyIfAbsent ? "nx" : "set", p.GateSeen, id, p.Cid, p.NewNonce, p.FenceTtlMilliseconds, Ttl(p.Tcap)]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        if (IsClosed(p.GateSeen))
        {
            // A closed gate seen equals the closed gate stored, so the gate condition alone would let this publish through.
            if (!await _db.KeyExistsAsync(staging, Master).ConfigureAwait(false))
            {
                return FenceMoved;
            }

            await _db.KeyDeleteAsync(staging, Master).ConfigureAwait(false);
            return await HoldsIdentityAsync(keys.Control, id).ConfigureAwait(false) ? GateRefused : IdentityMismatch;
        }

        long? deadline = null;
        if (p.Px != NoExpiry)
        {
            deadline = p.Tcap + p.Px;
            if (deadline - await ServerTimeAsync(keys.Data).ConfigureAwait(false) < 1)
            {
                await _db.KeyDeleteAsync(staging, Master).ConfigureAwait(false);
                return PastDeadline;
            }
        }

        return await WatchedAsync(() => TryPublishHashAsync(keys, staging, id, p, deadline)).ConfigureAwait(false);
    }

    /// <summary>BUMP: replaces the fence in the same step as the writer's Dst write or delete.</summary>
    /// <returns>-1, the delete count for <see cref="BumpOperation.Delete"/>, otherwise 1; any value of 0 or more is committed.</returns>
    public async Task<long> BumpAsync(FencedKeys keys, string id, string nonce, long fenceTtlMilliseconds, BumpOperation operation, RedisValue value = default, long px = NoExpiry)
    {
        CheckLife(px, nameof(px));
        var op = operation switch { BumpOperation.Set => "set", BumpOperation.Delete => "del", _ => "none" };
        var reply = await EvaluateAsync(BumpScript, [keys.Fence, keys.Data, keys.Control], [fenceTtlMilliseconds, op, value.IsNull ? RedisValue.EmptyString : value, Ttl(px), nonce, id]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(() => TryBumpAsync(keys, id, nonce, fenceTtlMilliseconds, operation, value, px)).ConfigureAwait(false);
    }

    /// <summary>FILL: creates the destination when absent and replaces the fence, so a captured promoter cannot overwrite the fill.</summary>
    /// <returns>-1, 2 when the destination exists, 1.</returns>
    public async Task<long> FillAsync(FencedKeys keys, string id, RedisValue value, long px, string nonce, long fenceTtlMilliseconds)
    {
        CheckLife(px, nameof(px));
        var reply = await EvaluateAsync(FillScript, [keys.Fence, keys.Data, keys.Control], [value, Ttl(px), nonce, fenceTtlMilliseconds, id]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.HashEqual(keys.Control, "id", id));
            transaction.AddCondition(Condition.KeyNotExists(keys.Data));
            queued.Add(transaction.StringSetAsync(keys.Data, value, px == NoExpiry ? null : TimeSpan.FromMilliseconds(px), When.Always, Master));
            queued.Add(transaction.StringSetAsync(keys.Fence, nonce, TimeSpan.FromMilliseconds(fenceTtlMilliseconds), When.Always, Master));
            if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
            {
                return Published;
            }

            if (!await HoldsIdentityAsync(keys.Control, id).ConfigureAwait(false))
            {
                return IdentityMismatch;
            }

            return await _db.KeyExistsAsync(keys.Data, Master).ConfigureAwait(false) ? DestinationExists : null;
        }).ConfigureAwait(false);
    }

    /// <summary>FILLH: renames a filled staging hash over an absent destination with a relative life, and replaces the fence.</summary>
    /// <returns>-1, 3 when staging is missing, 2 when the destination exists (staging is then deleted), 1.</returns>
    public async Task<long> FillHashAsync(FencedKeys keys, RedisKey staging, string id, long px, string nonce, long fenceTtlMilliseconds)
    {
        CheckLife(px, nameof(px));
        var result = await FillHashCoreAsync(keys, staging, id, px, nonce, fenceTtlMilliseconds).ConfigureAwait(false);
        if (result == DestinationExists)
        {
            await _db.KeyDeleteAsync(staging, Master).ConfigureAwait(false);
        }

        return result;
    }

    private static bool IsClosed(string gate) => gate.StartsWith("CLOSED", StringComparison.Ordinal);

    private static void AddPublishConditions(ITransaction transaction, FencedKeys keys, string id, Publication p)
    {
        transaction.AddCondition(Condition.HashEqual(keys.Control, "id", id));
        transaction.AddCondition(Condition.HashEqual(keys.Control, "c:" + p.Cid, p.GateSeen));
        transaction.AddCondition(Condition.StringEqual(keys.Fence, p.Token));
        if (p.OnlyIfAbsent)
        {
            transaction.AddCondition(Condition.KeyNotExists(keys.Data));
        }
    }

    private async Task<long?> TryPublishHashAsync(FencedKeys keys, RedisKey staging, string id, Publication p, long? deadline)
    {
        var transaction = _db.CreateTransaction();
        var queued = new List<Task>();
        AddPublishConditions(transaction, keys, id, p);
        transaction.AddCondition(Condition.KeyExists(staging));
        queued.Add(transaction.KeyRenameAsync(staging, keys.Data, When.Always, Master));
        if (deadline is { } at)
        {
            queued.Add(transaction.KeyExpireAsync(keys.Data, At(at), Master));
        }
        else
        {
            queued.Add(transaction.KeyPersistAsync(keys.Data, Master));
        }

        queued.Add(transaction.StringSetAsync(keys.Fence, p.NewNonce, TimeSpan.FromMilliseconds(p.FenceTtlMilliseconds), When.Always, Master));
        if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
        {
            return Published;
        }

        if (!await _db.KeyExistsAsync(staging, Master).ConfigureAwait(false))
        {
            return FenceMoved;
        }

        var refusal = await PublishRefusalAsync(keys, id, p).ConfigureAwait(false);
        if (refusal is not null)
        {
            await _db.KeyDeleteAsync(staging, Master).ConfigureAwait(false);
        }

        return refusal;
    }

    private async Task<long?> TryBumpAsync(FencedKeys keys, string id, string nonce, long fenceTtlMilliseconds, BumpOperation operation, RedisValue value, long px)
    {
        var transaction = _db.CreateTransaction();
        var queued = new List<Task>();
        transaction.AddCondition(Condition.HashEqual(keys.Control, "id", id));
        queued.Add(transaction.StringSetAsync(keys.Fence, nonce, TimeSpan.FromMilliseconds(fenceTtlMilliseconds), When.Always, Master));
        Task<bool>? deleted = null;
        if (operation == BumpOperation.Delete)
        {
            deleted = transaction.KeyDeleteAsync(keys.Data, Master);
        }
        else if (operation == BumpOperation.Set)
        {
            queued.Add(transaction.StringSetAsync(keys.Data, value, px == NoExpiry ? null : TimeSpan.FromMilliseconds(px), When.Always, Master));
        }

        if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
        {
            if (deleted is null)
            {
                return 1;
            }

            return await deleted.ConfigureAwait(false) ? 1 : 0;
        }

        return await HoldsIdentityAsync(keys.Control, id).ConfigureAwait(false) ? null : IdentityMismatch;
    }

    private async Task<long> FillHashCoreAsync(FencedKeys keys, RedisKey staging, string id, long px, string nonce, long fenceTtlMilliseconds)
    {
        var reply = await EvaluateAsync(FillHashScript, [staging, keys.Data, keys.Fence, keys.Control], [id, Ttl(px), nonce, fenceTtlMilliseconds]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            transaction.AddCondition(Condition.HashEqual(keys.Control, "id", id));
            transaction.AddCondition(Condition.KeyNotExists(keys.Data));
            transaction.AddCondition(Condition.KeyExists(staging));
            queued.Add(transaction.KeyRenameAsync(staging, keys.Data, When.Always, Master));
            if (px == NoExpiry)
            {
                queued.Add(transaction.KeyPersistAsync(keys.Data, Master));
            }
            else
            {
                queued.Add(transaction.KeyExpireAsync(keys.Data, TimeSpan.FromMilliseconds(px), Master));
            }

            queued.Add(transaction.StringSetAsync(keys.Fence, nonce, TimeSpan.FromMilliseconds(fenceTtlMilliseconds), When.Always, Master));
            if (await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
            {
                return Published;
            }

            if (!await HoldsIdentityAsync(keys.Control, id).ConfigureAwait(false))
            {
                return IdentityMismatch;
            }

            if (!await _db.KeyExistsAsync(staging, Master).ConfigureAwait(false))
            {
                return StagingMissing;
            }

            return await _db.KeyExistsAsync(keys.Data, Master).ConfigureAwait(false) ? DestinationExists : null;
        }).ConfigureAwait(false);
    }

    // In order on one connection: the token read last is the one in force, and nothing here needs atomicity, so the fence is set and refreshed
    // even when the identity or the gate then refuses. Null when no token was read.
    private async Task<Capture?> CaptureInBatchAsync(FencedKeys keys, string id, string cid, string nonce, long fenceTtlMilliseconds)
    {
        var batch = _db.CreateBatch();
        var heldId = batch.HashGetAsync(keys.Control, "id", Master);
        var gate = batch.HashGetAsync(keys.Control, "c:" + cid, Master);
        var created = batch.StringSetAsync(keys.Fence, nonce, null, When.NotExists, Master);
        var refreshed = batch.KeyExpireAsync(keys.Fence, TimeSpan.FromMilliseconds(fenceTtlMilliseconds), Master);
        var token = batch.StringGetAsync(keys.Fence, Master);
        var exists = batch.KeyExistsAsync(keys.Data, Master);
        batch.Execute();
        await Task.WhenAll(heldId, gate, created, refreshed, token, exists).ConfigureAwait(false);
        if (heldId.Result != id)
        {
            return new Capture(IdentityMismatch, null, false, null, 0);
        }

        if (gate.Result.IsNull || IsClosed(gate.Result!))
        {
            return new Capture(GateRefused, null, false, null, 0);
        }

        if (token.Result.IsNull)
        {
            return null;
        }

        var tcap = await ServerTimeAsync(keys.Data).ConfigureAwait(false);
        return new Capture(0, token.Result, exists.Result, gate.Result, tcap);
    }

    // The predicates in the script's order after an aborted EXEC; null when all still hold, which was a WATCH abort.
    private async Task<long?> PublishRefusalAsync(FencedKeys keys, string id, Publication p)
    {
        var batch = _db.CreateBatch();
        var heldId = batch.HashGetAsync(keys.Control, "id", Master);
        var gate = batch.HashGetAsync(keys.Control, "c:" + p.Cid, Master);
        var token = batch.StringGetAsync(keys.Fence, Master);
        var exists = batch.KeyExistsAsync(keys.Data, Master);
        batch.Execute();
        await Task.WhenAll(heldId, gate, token, exists).ConfigureAwait(false);
        if (heldId.Result != id)
        {
            return IdentityMismatch;
        }

        if (gate.Result != p.GateSeen)
        {
            return GateRefused;
        }

        if (token.Result != p.Token)
        {
            return FenceMoved;
        }

        return p.OnlyIfAbsent && exists.Result ? DestinationExists : null;
    }
}
