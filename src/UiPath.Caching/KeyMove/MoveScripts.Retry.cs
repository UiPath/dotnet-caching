using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    /// <summary>The fence no longer holds either token a retried write may carry: a newer write or a promotion came first, or the fence expired.</summary>
    public const long Superseded = -6;

    private const string BumpIfScript = """
        if redis.call('HGET', KEYS[3], 'id') ~= ARGV[6] then return -1 end
        local g = redis.call('GET', KEYS[1])
        if (not g) or (g ~= ARGV[5] and g ~= ARGV[7]) then return -6 end
        redis.call('SET', KEYS[1], ARGV[5], 'PX', ARGV[1])
        if ARGV[2] == 'del' then
          if ARGV[8] ~= '' then redis.call('HDEL', KEYS[3], ARGV[8]) end
          return redis.call('DEL', KEYS[2])
        end
        if ARGV[4] == '-1' then redis.call('SET', KEYS[2], ARGV[3]) else redis.call('SET', KEYS[2], ARGV[3], 'PX', ARGV[4]) end
        return 1
        """;

    /// <summary>
    /// BUMPIF: the retry of a BUMP set or delete. It writes only while the fence equals <paramref name="nonce"/> (what the first attempt set) or <paramref name="observed"/>
    /// (the token seen just before it), and leaves the fence at <paramref name="nonce"/>. An absent fence is superseded: it expires, so absence proves nothing.
    /// </summary>
    /// <param name="markerField">For a delete, the marker field the delete also removes.</param>
    /// <returns>-1 identity, -6 superseded, for a delete the delete count, otherwise 1.</returns>
    public async Task<long> BumpIfFenceAsync(FencedKeys keys, string id, string nonce, string? observed, long fenceTtlMilliseconds, BumpOperation operation, RedisValue value = default, long px = NoExpiry, string? markerField = null)
    {
        CheckLife(px, nameof(px));
        var op = operation == BumpOperation.Delete ? "del" : "set";

        // An unknown observed token is a value no fence holds, so only the nonce can match.
        var other = observed ?? string.Empty;
        var field = operation == BumpOperation.Delete ? markerField ?? string.Empty : string.Empty;
        var reply = await EvaluateAsync(BumpIfScript, [keys.Fence, keys.Data, keys.Control], [fenceTtlMilliseconds, op, value.IsNull ? RedisValue.EmptyString : value, Ttl(px), nonce, id, other, field]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(() => TryBumpIfFenceAsync(keys, id, nonce, other, fenceTtlMilliseconds, operation, value, px, field)).ConfigureAwait(false);
    }

    private async Task<long?> TryBumpIfFenceAsync(FencedKeys keys, string id, string nonce, string other, long fenceTtlMilliseconds, BumpOperation operation, RedisValue value, long px, string field)
    {
        if (!await HoldsIdentityAsync(keys.Control, id).ConfigureAwait(false))
        {
            return IdentityMismatch;
        }

        string? held = await _db.StringGetAsync(keys.Fence, Master).ConfigureAwait(false);
        if (held is null || (held != nonce && held != other))
        {
            return Superseded;
        }

        var transaction = _db.CreateTransaction();
        var queued = new List<Task>();
        transaction.AddCondition(Condition.HashEqual(keys.Control, "id", id));
        transaction.AddCondition(Condition.StringEqual(keys.Fence, held));
        queued.Add(transaction.StringSetAsync(keys.Fence, nonce, TimeSpan.FromMilliseconds(fenceTtlMilliseconds), When.Always, Master));
        Task<bool>? deleted = null;
        if (operation == BumpOperation.Delete)
        {
            if (field.Length > 0)
            {
                queued.Add(transaction.HashDeleteAsync(keys.Control, field, Master));
            }

            deleted = transaction.KeyDeleteAsync(keys.Data, Master);
        }
        else
        {
            queued.Add(transaction.StringSetAsync(keys.Data, value, px == NoExpiry ? null : TimeSpan.FromMilliseconds(px), When.Always, Master));
        }


        // On a watch abort the next pass distinguishes an identity change from a fence change.
        if (!await ExecuteAllAsync(transaction, queued).ConfigureAwait(false))
        {
            return null;
        }

        if (deleted is null)
        {
            return 1;
        }

        return await deleted.ConfigureAwait(false) ? 1 : 0;
    }
}
