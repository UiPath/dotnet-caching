using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache : IFencedFillTier
{
    /// <summary>Persists the key unless it holds a tombstone, in one step.</summary>
    private const string PersistUnlessTombstoneScript = "local ok, h = pcall(redis.call, 'GETRANGE', KEYS[1], 0, 1) if ok then local a, b = string.byte(h, 1, 2) if a == 15 and b and b >= 48 and b <= 57 then return 0 end end return redis.call('PERSIST', KEYS[1])";

    /// <summary>Sets the key's deadline (ARGV[1], Unix milliseconds) unless it holds a tombstone, in one step.</summary>
    private const string ExpireUnlessTombstoneScript = "local ok, h = pcall(redis.call, 'GETRANGE', KEYS[1], 0, 1) if ok then local a, b = string.byte(h, 1, 2) if a == 15 and b and b >= 48 and b <= 57 then return 0 end end return redis.call('PEXPIREAT', KEYS[1], ARGV[1])";

    private enum UnreadableReplace
    {
        Stored,
        Changed,
        Refused,
    }

    public bool IsFenced => _tombstoneGrace is not null;

    /// <summary>Runs after a fill has judged a tombstone replaceable and before it swaps it; tests land a removal there.</summary>
    internal Func<Task>? BeforeTombstoneSwap { get; set; }

    /// <summary>Runs after a refresh without scripts has inspected the key and before it changes it; tests land a removal there.</summary>
    internal Func<Task>? BeforeRefreshChange { get; set; }

    /// <summary>Runs after a fill has read a value it cannot read and before it replaces those bytes; tests land a removal there.</summary>
    internal Func<Task>? BeforeUnreadableReplace { get; set; }

    /// <summary>Runs when a fill's read has been answered with a tombstone and before the age is judged; tests let time pass there, as it does while the answer is in flight.</summary>
    internal Action? AfterFillRead { get; set; }

    private protected override Task<TimeSpan?> TimeToLiveOfAsync(RedisKey key) =>
        _tombstoneGrace is null ? base.TimeToLiveOfAsync(key) : TimeToLiveUnlessTombstoneAsync(key);

    private protected override Task<DateTime?> ExpireTimeOfAsync(RedisKey key) =>
        _tombstoneGrace is null ? base.ExpireTimeOfAsync(key) : ExpireTimeUnlessTombstoneAsync(key);

    /// <summary>A refresh that is not awaited answers nothing.</summary>
    private static bool IsOne(RedisResult? reply) => reply is { IsNull: false } && (long)reply == 1;

    private static bool IsTombstoneHead(RedisValue head) => !head.IsNull && TombstoneValue.IsHead(((ReadOnlyMemory<byte>)head).Span);

    private static RedisValue NewTombstone(TimeSpan grace) => TombstoneValue.Create((long)grace.TotalMilliseconds);

    private async Task<TimeSpan?> TimeToLiveUnlessTombstoneAsync(RedisKey key)
    {
        var flags = ReadFlagsFor(key);
        var batch = Database.CreateBatch();
        var timeToLive = batch.KeyTimeToLiveAsync(key, flags);
        var head = batch.StringGetRangeAsync(key, 0, 1, flags);
        batch.Execute();
        try
        {
            var left = await timeToLive.ConfigureAwait(false);
            if (!IsTombstoneHead(await head.ConfigureAwait(false)))
            {
                return left;
            }
        }
        catch
        {
            head.Observe();
            throw;
        }

        await RepairTombstoneAsync(key).ConfigureAwait(false);
        return null;
    }

    private async Task<DateTime?> ExpireTimeUnlessTombstoneAsync(RedisKey key)
    {
        var flags = ReadFlagsFor(key);
        var batch = Database.CreateBatch();
        var expiration = batch.KeyExpireTimeAsync(key, flags);
        var head = batch.StringGetRangeAsync(key, 0, 1, flags);
        batch.Execute();
        try
        {
            var at = await expiration.ConfigureAwait(false);
            if (!IsTombstoneHead(await head.ConfigureAwait(false)))
            {
                return at;
            }
        }
        catch
        {
            head.Observe();
            throw;
        }

        await RepairTombstoneAsync(key).ConfigureAwait(false);
        return null;
    }

    private Task<bool> RemoveKeyAsync(RedisKey key) =>
        _tombstoneGrace is { } grace
            ? Database.StringSetAsync(key, NewTombstone(grace), grace, When.Always, CommandFlags.DemandMaster)
            : Database.KeyDeleteAsync(key, CommandFlags.DemandMaster);

    /// <summary>The number of keys removed, or -1 when the transaction did not commit.</summary>
    private Task<long> RemoveKeysAsync(RedisKey[] keys) =>
        _tombstoneGrace is null ? Database.KeyDeleteAsync(keys, CommandFlags.DemandMaster) : TombstoneKeysAsync(keys);

    private void QueueRemove(ITransaction transaction, RedisKey key)
    {
        if (_tombstoneGrace is { } grace)
        {
            transaction.StringSetAsync(key, NewTombstone(grace), grace, When.Always, CommandFlags.DemandMaster).Forget();
        }
        else
        {
            transaction.KeyDeleteAsync(key, CommandFlags.DemandMaster).Forget();
        }
    }

    private async Task<long> TombstoneKeysAsync(RedisKey[] keys)
    {
        var transaction = Database.CreateTransaction(asyncState: null);
        foreach (var key in keys)
        {
            QueueRemove(transaction, key);
        }

        return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false) ? keys.Length : -1;
    }

    private Task<bool> ExistsAsync(RedisKey key) =>
        _tombstoneGrace is null ? Database.KeyExistsAsync(key, ReadFlagsFor(key)) : ExistsUnlessTombstoneAsync(key);

    private async Task<bool> ExistsUnlessTombstoneAsync(RedisKey key)
    {
        var flags = ReadFlagsFor(key);
        var batch = Database.CreateBatch();
        var exists = batch.KeyExistsAsync(key, flags);
        var head = batch.StringGetRangeAsync(key, 0, 1, flags);
        batch.Execute();
        try
        {
            return await exists.ConfigureAwait(false) && !IsTombstoneHead(await head.ConfigureAwait(false));
        }
        catch
        {
            head.Observe();
            throw;
        }
    }

    /// <summary>One step with scripts; without, a transaction that applies only while the key still holds what was inspected, so a tombstone is never changed.</summary>
    private async ValueTask<bool> PersistUnlessTombstoneAsync(RedisKey key)
    {
        if (RewriteScriptsOn)
        {
            try
            {
                return IsOne(await Database.ScriptEvaluateAsync(PersistUnlessTombstoneScript, [key], flags: RefreshFlags).ConfigureAwait(false));
            }
            catch (Exception ex) when (RedisUtils.RefusesScript(ex))
            {
                _rewriteScriptRefused(ex);
            }
        }

        return await RefreshUnlessTombstoneAsync(key, null).ConfigureAwait(false);
    }

    private async ValueTask<bool> ExpireUnlessTombstoneAsync(RedisKey key, DateTime expiration)
    {
        if (RewriteScriptsOn)
        {
            try
            {
                var at = new DateTimeOffset(expiration.ToUniversalTime()).ToUnixTimeMilliseconds();
                return IsOne(await Database.ScriptEvaluateAsync(ExpireUnlessTombstoneScript, [key], [at], RefreshFlags).ConfigureAwait(false));
            }
            catch (Exception ex) when (RedisUtils.RefusesScript(ex))
            {
                _rewriteScriptRefused(ex);
            }
        }

        return await RefreshUnlessTombstoneAsync(key, expiration).ConfigureAwait(false);
    }

    /// <summary>Changes the key's deadline in a transaction that commits only while the key is as inspected; a tombstone seen is put back to its grace and the refresh is refused.</summary>
    private async ValueTask<bool> RefreshUnlessTombstoneAsync(RedisKey key, DateTime? expiration)
    {
        var head = await Database.StringGetRangeAsync(key, 0, TombstoneValue.MaxLength, CommandFlags.DemandMaster).ConfigureAwait(false);
        if (IsTombstoneHead(head))
        {
            await RepairTombstoneAsync(key).ConfigureAwait(false);
            return false;
        }

        if (BeforeRefreshChange is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        var transaction = Database.CreateTransaction(asyncState: null);
        // A value seen as long is held to the length condition; a short one is compared whole, so a larger replacement can still meet it.
        transaction.AddCondition(head.Length() > TombstoneValue.MaxLength ? Condition.StringLengthGreaterThan(key, TombstoneValue.MaxLength) : Condition.StringEqual(key, head));
        if (expiration is { } at)
        {
            transaction.KeyExpireAsync(key, at, CommandFlags.DemandMaster).Forget();
        }
        else
        {
            transaction.KeyPersistAsync(key, CommandFlags.DemandMaster).Forget();
        }

        return await transaction.ExecuteAsync(RefreshFlags).ConfigureAwait(false);
    }
    private async ValueTask<bool> HoldsTombstoneAsync(RedisKey key) =>
        IsTombstoneHead(await Database.StringGetRangeAsync(key, 0, 1, CommandFlags.DemandMaster).ConfigureAwait(false));

    /// <summary>A tombstone with no deadline or one past its grace goes back to its grace; a newer removal stands.</summary>
    private async ValueTask RepairTombstoneAsync(RedisKey key)
    {
        try
        {
            var (held, left) = await ReadWithLifetimeAsync(key).ConfigureAwait(false);
            if (TombstoneValue.TryRead(held, out var grace) && (left is not { } remaining || remaining.TotalMilliseconds > grace))
            {
                await RestoreGraceQuietlyAsync(key, held, grace).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            LogRedisCacheException(ex);
        }
    }

    private async ValueTask RestoreGraceAsync(RedisKey key, RedisValue held, long grace) =>
        await FormatRewriteScripts.ReplaceStringAsync(Database, key, held, held, () => TimeSpan.FromMilliseconds(grace), RewriteScriptsOn, _rewriteScriptRefused).ConfigureAwait(false);

    /// <summary>As <see cref="RestoreGraceAsync"/>; a failure is logged, not thrown.</summary>
    private async ValueTask RestoreGraceQuietlyAsync(RedisKey key, RedisValue held, long grace)
    {
        try
        {
            await RestoreGraceAsync(key, held, grace).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            LogRedisCacheException(ex);
        }
    }

    /// <summary>The key's value and its remaining lifetime as one snapshot; no lifetime for a key with none.</summary>
    private async ValueTask<(RedisValue Value, TimeSpan? Left)> ReadWithLifetimeAsync(RedisKey key)
    {
        var transaction = Database.CreateTransaction();
        var value = transaction.StringGetAsync(key, CommandFlags.DemandMaster);
        var timeToLive = transaction.KeyTimeToLiveAsync(key, CommandFlags.DemandMaster);
        if (!await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The read transaction was not committed");
        }

        return (await value.ConfigureAwait(false), await timeToLive.ConfigureAwait(false));
    }

    /// <summary>SET NX, then a compare-and-set over the tombstone if that is what held the key: a removed key is absent.</summary>
    private async ValueTask<bool> AddOverTombstoneAsync(RedisKey key, RedisValue serialized, TimeSpan expiration)
    {
        for (var attempt = 0; attempt < FillAttempts; attempt++)
        {
            if (await Database.StringSetAsync(key, serialized, expiration, When.NotExists, CommandFlags.DemandMaster).ConfigureAwait(false))
            {
                return true;
            }

            var held = await ReadTombstoneAsync(key).ConfigureAwait(false);
            if (held.IsNull)
            {
                // Gone since the NX lost: another attempt; otherwise a value holds the key.
                if (await Database.KeyExistsAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false))
                {
                    return false;
                }

                continue;
            }

            if (await FormatRewriteScripts.ReplaceStringAsync(Database, key, held, serialized, () => expiration, RewriteScriptsOn, _rewriteScriptRefused).ConfigureAwait(false) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Replaces the unreadable bytes the key holds, and only those: a removal or write that landed since has changed the key, which is <see cref="UnreadableReplace.Changed"/> for the fill to decide again.</summary>
    private async ValueTask<UnreadableReplace> ReplaceUnreadableAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, T? value, DateTimeOffset deadline, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();
        NoteWritten(redisKey);
        try
        {
            var held = await Database.StringGetAsync(redisKey, CommandFlags.DemandMaster).ConfigureAwait(false);
            if (held.IsNull || TombstoneValue.Is(held) || ReadStored<T, TFormat>(redisKey, held, format).Found)
            {
                return UnreadableReplace.Changed;
            }

            if (BeforeUnreadableReplace is { } hook)
            {
                await hook().ConfigureAwait(false);
            }

            var isNull = IsDefault(value);
            var (serialized, payload) = isNull ? (RedisValue.EmptyString, default(SerializedPayload)) : SerializeValue(value, format);
            _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), serialized);
            using (payload)
            {
                return await FormatRewriteScripts.ReplaceStringAsync(Database, redisKey, held, serialized, () => RemainingOrZero(deadline), RewriteScriptsOn, _rewriteScriptRefused).ConfigureAwait(false) switch
                {
                    true => UnreadableReplace.Stored,
                    false => UnreadableReplace.Changed,
                    null => UnreadableReplace.Refused,
                };
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            LogRedisCacheException(ex);
            return UnreadableReplace.Refused;
        }
        finally
        {
            NoteWritten(redisKey);
        }
    }
    /// <summary>The tombstone the key holds, or null; the value is read in full only when its start is the tombstone's.</summary>
    private async ValueTask<RedisValue> ReadTombstoneAsync(RedisKey key)
    {
        if (!await HoldsTombstoneAsync(key).ConfigureAwait(false))
        {
            return RedisValue.Null;
        }

        var held = await Database.StringGetAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
        return TombstoneValue.Is(held) ? held : RedisValue.Null;
    }
}
