using StackExchange.Redis;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    /// <summary>The reply of the chunk script for a key that holds something other than a hash.</summary>
    private const long NotAHash = -9;

    /// <summary>How often the fallback transaction is sent again when its watch aborts it, as the move scripts do.</summary>
    private const int MaxChunkWatchAborts = 16;

    // KEYS 1 hash. ARGV 1 absolute expiry in Unix milliseconds or -1, then field and value pairs. Nothing is changed for a key of another type. The fields go in before the expiry: an expiry that has passed deletes the key.
    private const string GuardedChunkScript = """
        local t = redis.call('TYPE', KEYS[1])['ok']
        if t ~= 'none' and t ~= 'hash' then return -9 end
        for i = 2, #ARGV, 2 do redis.call('HSET', KEYS[1], ARGV[i], ARGV[i + 1]) end
        if ARGV[1] == '-1' then redis.call('PERSIST', KEYS[1]) else redis.call('PEXPIREAT', KEYS[1], ARGV[1]) end
        return 1
        """;

    private static readonly Version FallbackMinimum = new(6, 0, 9);

    private int _chunkScriptsRefused;

    /// <summary>Runs the chunk as the guarded script; false when the server refused the script and the fallback has to write it.</summary>
    private async Task<bool> TryWriteGuardedChunkScriptAsync(RedisKey target, HashEntry[] chunk, long expireAt)
    {
        var arguments = new RedisValue[(chunk.Length * 2) + 1];
        arguments[0] = expireAt;
        for (var i = 0; i < chunk.Length; i++)
        {
            arguments[(i * 2) + 1] = chunk[i].Name;
            arguments[(i * 2) + 2] = chunk[i].Value;
        }

        try
        {
            var reply = await Database.ScriptEvaluateAsync(GuardedChunkScript, [target], arguments, CommandFlags.DemandMaster).ConfigureAwait(false);
            if ((long)reply == NotAHash)
            {
                throw new HashTypeRefusedException(target);
            }

            return true;
        }
        catch (Exception ex) when (RedisUtils.ScriptNeverRan(ex))
        {
            // Only a refusal of the EVAL itself falls back: an error from inside the running script follows commands that may have applied, so it is an unknown write and goes to the caller.
            Interlocked.Exchange(ref _chunkScriptsRefused, 1);
        }

        return false;
    }

    /// <summary>Writes one chunk of a merge in one step that first checks the key is a hash or absent: a script, or where the server refuses scripts a watched transaction (which needs 6.0.9, where a watched key that expires aborts it).</summary>
    /// <exception cref="HashTypeRefusedException">The key holds another type; nothing was changed.</exception>
    private async Task WriteGuardedChunkAsync(RedisKey target, HashEntry[] chunk, DateTimeOffset expiration)
    {
        var expireAt = expiration == DateTimeOffset.MaxValue ? -1 : expiration.ToUnixTimeMilliseconds();
        if (ChunkScriptsEnabled && Volatile.Read(ref _chunkScriptsRefused) == 0 && await TryWriteGuardedChunkScriptAsync(target, chunk, expireAt).ConfigureAwait(false))
        {
            return;
        }

        if (ServerVersion < FallbackMinimum)
        {
            throw new NotSupportedException($"The server refuses scripts and runs Redis {ServerVersion}; a guarded merge without scripts needs {FallbackMinimum} or later.");
        }

        for (var attempt = 0; attempt < MaxChunkWatchAborts; attempt++)
        {
            var type = await Database.KeyTypeAsync(target, CommandFlags.DemandMaster).ConfigureAwait(false);
            if (type is not (RedisType.None or RedisType.Hash))
            {
                throw new HashTypeRefusedException(target);
            }

            // The condition watches the key: a change of its type, or of whether it is there, aborts the transaction and the type is read again.
            var transaction = Database.CreateTransaction();
            transaction.AddCondition(type == RedisType.Hash ? Condition.HashLengthGreaterThan(target, 0) : Condition.KeyNotExists(target));
            var written = transaction.HashSetAsync(target, chunk, CommandFlags.DemandMaster);
            var lifetime = expireAt == -1
                ? (Task)transaction.KeyPersistAsync(target, CommandFlags.DemandMaster)
                : transaction.KeyExpireAsync(target, expiration.UtcDateTime, CommandFlags.DemandMaster);
            if (await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false))
            {
                await ObserveAllAsync([written, lifetime]).ConfigureAwait(false);
                return;
            }

            written.Forget();
            lifetime.Forget();
        }

        throw new RedisException($"A chunk of {target} was aborted {MaxChunkWatchAborts} times while its conditions held; the outcome is unknown.");
    }
}
