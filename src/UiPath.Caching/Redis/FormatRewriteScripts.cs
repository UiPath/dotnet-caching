using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace UiPath.Caching.Redis;

/// <summary>
/// Rewrites a stored value only while it still holds the bytes that were read, so a write that raced the rewrite is never overwritten.
/// The caller decides what to send (the format the nodes can read now, not the one a policy wants) and whether a value is too large to compare,
/// and keeps scripts off once <c>scriptRefused</c> is told, since a refused call costs a failed round trip before the fallback.
/// </summary>
internal static class FormatRewriteScripts
{
    private const string StringScript =
        "local v = redis.call('GET', KEYS[1]) if v and redis.sha1hex(v) == ARGV[1] then redis.call('SET', KEYS[1], ARGV[2], 'KEEPTTL') return 1 end return 0";

    // ARGV holds a field, the digest of the bytes it was read with and the new value, per field.
    private const string HashScript =
        "local n = 0 for i = 1, #ARGV, 3 do local v = redis.call('HGET', KEYS[1], ARGV[i]) if v and redis.sha1hex(v) == ARGV[i + 1] then redis.call('HSET', KEYS[1], ARGV[i], ARGV[i + 2]) n = n + 1 end end return n";

    // The fallback sends this many fields at once; each is a transaction that watches the whole hash.
    private const int FallbackBatch = 8;

    private static readonly byte[] StringScriptHash = SHA1.HashData(Encoding.UTF8.GetBytes(StringScript));
    private static readonly byte[] HashScriptHash = SHA1.HashData(Encoding.UTF8.GetBytes(HashScript));

    /// <summary>Whether the key held <paramref name="expected"/> and now holds <paramref name="value"/>, with the TTL it had; a key that changed, went away or holds another type is left alone and never created.</summary>
    /// <param name="useScripts">False sends a transaction that checks the value instead, for a server that refuses scripts.</param>
    /// <param name="scriptRefused">Told when the server refuses the script, which then falls back to the transaction for this call.</param>
    public static async Task<bool> RewriteStringAsync(
        IDatabase db, RedisKey key, ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> value, bool useScripts = true, Action<Exception>? scriptRefused = null)
    {
        if (useScripts)
        {
            try
            {
                var reply = await EvaluateAsync(db, StringScript, StringScriptHash, key, [Sha1Hex(expected), value]).ConfigureAwait(false);
                return (long)reply == 1;
            }
            catch (Exception ex) when (IsWrongType(ex))
            {
                return false;
            }
            catch (Exception ex) when (RedisUtils.RefusesScript(ex))
            {
                scriptRefused?.Invoke(ex);
            }
        }

        var transaction = db.CreateTransaction(asyncState: null);
        // The server-side compare-and-set needs Redis 8.4; this library supports 6.
        transaction.AddCondition(Condition.StringEqual(key, expected));
        transaction.StringSetAsync(key, value, expiry: null, keepTtl: true, When.Always, CommandFlags.DemandMaster).Forget();
        return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
    }

    /// <summary>How many fields held their expected bytes and now hold the new ones; a field that changed or is missing is skipped, and a hash with none of them, or a key of another type, is never created or changed.</summary>
    /// <param name="fields">Each field once, and none of the reserved ones: the version counter and the metadata are not serialized values.</param>
    /// <param name="useScripts">False sends one transaction per field instead, which skips a field the same way.</param>
    /// <param name="scriptRefused">Told when the server refuses the script, which then falls back to the transactions for this call.</param>
    public static async Task<int> RewriteHashFieldsAsync(
        IDatabase db,
        RedisKey key,
        IReadOnlyList<(RedisValue Field, ReadOnlyMemory<byte> Expected, ReadOnlyMemory<byte> Value)> fields,
        bool useScripts = true,
        Action<Exception>? scriptRefused = null)
    {
        Validate(fields);
        if (fields.Count == 0)
        {
            return 0;
        }

        if (useScripts)
        {
            try
            {
                var arguments = new RedisValue[fields.Count * 3];
                for (var i = 0; i < fields.Count; i++)
                {
                    arguments[i * 3] = fields[i].Field;
                    arguments[(i * 3) + 1] = Sha1Hex(fields[i].Expected);
                    arguments[(i * 3) + 2] = fields[i].Value;
                }

                var reply = await EvaluateAsync(db, HashScript, HashScriptHash, key, arguments).ConfigureAwait(false);
                return (int)reply;
            }
            catch (Exception ex) when (IsWrongType(ex))
            {
                return 0;
            }
            catch (Exception ex) when (RedisUtils.RefusesScript(ex))
            {
                scriptRefused?.Invoke(ex);
            }
        }

        var rewritten = 0;
        for (var start = 0; start < fields.Count; start += FallbackBatch)
        {
            var batch = new List<Task<bool>>(FallbackBatch);
            for (var i = start; i < Math.Min(start + FallbackBatch, fields.Count); i++)
            {
                batch.Add(RewriteFieldAsync(db, key, fields[i].Field, fields[i].Expected, fields[i].Value));
            }

            rewritten += (await Task.WhenAll(batch).ConfigureAwait(false)).Count(done => done);
        }

        return rewritten;
    }

    private static void Validate(IReadOnlyList<(RedisValue Field, ReadOnlyMemory<byte> Expected, ReadOnlyMemory<byte> Value)> fields)
    {
        var seen = new HashSet<RedisValue>(fields.Count);
        foreach (var (field, _, _) in fields)
        {
            if (KnownFieldNames.IsReserved(field.ToString()))
            {
                throw new ArgumentException($"The field '{field}' is reserved and holds no serialized value.", nameof(fields));
            }

            if (!seen.Add(field))
            {
                throw new ArgumentException($"The field '{field}' is listed more than once.", nameof(fields));
            }
        }
    }

    // By digest, so the script text is not sent every call; a server that has not seen it, or forgot it, is sent the text once, which caches it again.
    private static async Task<RedisResult> EvaluateAsync(IDatabase db, string script, byte[] hash, RedisKey key, RedisValue[] values)
    {
        try
        {
            return await db.ScriptEvaluateAsync(hash, [key], values, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
        {
            return await db.ScriptEvaluateAsync(script, [key], values, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
    }

    // A condition on a key of another type fails, so a script says no as well rather than throw.
    private static bool IsWrongType(Exception ex) =>
        ex is RedisServerException && ex.Message.Contains("WRONGTYPE", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> RewriteFieldAsync(IDatabase db, RedisKey key, RedisValue field, ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> value)
    {
        var transaction = db.CreateTransaction(asyncState: null);
        transaction.AddCondition(Condition.HashEqual(key, field, expected));
        transaction.HashSetAsync(key, field, value, When.Always, CommandFlags.DemandMaster).Forget();
        return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
    }

    // redis.sha1hex is the only digest a script can compute, and it writes lower case.
    private static string Sha1Hex(ReadOnlyMemory<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(bytes.Span, hash);
        Span<char> text = stackalloc char[SHA1.HashSizeInBytes * 2];
        const string digits = "0123456789abcdef";
        for (var i = 0; i < hash.Length; i++)
        {
            text[i * 2] = digits[hash[i] >> 4];
            text[(i * 2) + 1] = digits[hash[i] & 0xF];
        }

        return new string(text);
    }
}
