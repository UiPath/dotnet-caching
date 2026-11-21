using System.Runtime.CompilerServices;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache
{
    /// <summary>Answers the value and remaining lifetime of an existing key, otherwise stores the value.</summary>
    private const string FillScript = "local v = redis.call('GET', KEYS[1]) if v then return {v, redis.call('PTTL', KEYS[1])} end if tonumber(ARGV[2]) > 0 then redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2]) else redis.call('SET', KEYS[1], ARGV[1]) end return false";

    private const int FillAttempts = 3;

    private const string ExpirationPassed = "the requested expiration passed before the write, so the entry would retain nothing";

    private enum ChangeStep
    {
        Settled,
        Stopped,
        Lost,
    }

    public ValueTask<FillResult<T>> TryFillAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryFillCoreAsync(cacheKey, value, GetExpiration(expiration), policy, token);

    public ValueTask<FillResult<T>> TryFillAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryFillCoreAsync(cacheKey, value, GetExpiration(expiration), policy, token);

    public ValueTask<ChangeResult<T>> SetIfChangedAsync<T>(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetIfChangedCoreAsync(cacheKey, value, comparer, GetExpiration(expiration), policy, token);

    public ValueTask<ChangeResult<T>> SetIfChangedAsync<T>(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetIfChangedCoreAsync(cacheKey, value, comparer, GetExpiration(expiration), policy, token);

    /// <summary>The attempts after a plain NX lost: one read of the winner, then whole attempts if it was gone again.</summary>
    private static async ValueTask<FillRaw?> FillAfterAsync(RedisCache self, RedisKey key, RedisValue serialized, DateTimeOffset until, bool lost, CancellationToken token)
    {
        var read = lost;
        for (var attempt = 0; attempt < FillAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            FillRaw? filled;
            if (read)
            {
                filled = self.RemainingOrZero(until) <= TimeSpan.Zero ? FillRaw.Lapsed : await ReadWinnerAsync(self, key).ConfigureAwait(false);
            }
            else
            {
                filled = await FillOnceAsync(self, key, serialized, until).ConfigureAwait(false);
            }

            read = false;
            if (filled is { } done)
            {
                return done;
            }
        }

        return null;
    }

    private static async ValueTask<FillRaw?> ReadWinnerAsync(RedisCache self, RedisKey key)
    {
        var read = new StrongBox<StrongBox<(RedisValue Value, DateTimeOffset? Expiration)>?>();
        await ReadOnceAsync(self, key, read, atomic: true).ConfigureAwait(false);
        var (value, deadline) = read.Value!.Value;
        // Gone again since the NX lost: the caller tries once more.
        return value.IsNull ? null : new FillRaw(false, value, self.Clock.ToDateTimeOffset(deadline));
    }

    private static async ValueTask<FillRaw?> FillOnceAsync(RedisCache self, RedisKey key, RedisValue serialized, DateTimeOffset until)
    {
        var expiration = self.RemainingOrZero(until);
        if (expiration <= TimeSpan.Zero)
        {
            return FillRaw.Lapsed;
        }

        if (self.RewriteScriptsOn)
        {
            try
            {
                var milliseconds = expiration == TimeSpan.MaxValue ? 0 : Math.Max(1, (long)Math.Ceiling(expiration.TotalMilliseconds));
                var reply = await self.Database.ScriptEvaluateAsync(FillScript, [key], [serialized, milliseconds], CommandFlags.DemandMaster).ConfigureAwait(false);
                if (reply.IsNull)
                {
                    return new FillRaw(true, RedisValue.Null, DateTimeOffset.MaxValue);
                }

                var parts = ((RedisResult[]?)reply)!;
                var ttl = (long)parts[1];
                return new FillRaw(false, (RedisValue)parts[0], self.Clock.ToDateTimeOffset(ttl < 0 ? null : TimeSpan.FromMilliseconds(ttl)));
            }
            catch (Exception ex) when (RedisUtils.RefusesScript(ex))
            {
                self._rewriteScriptRefused(ex);
            }
        }

        // A refused script cost a round trip, so the lifetime is worked out again.
        expiration = self.RemainingOrZero(until);
        if (expiration <= TimeSpan.Zero)
        {
            return FillRaw.Lapsed;
        }

        // SET NX is atomic by itself; the read after a lost NX is a snapshot.
        if (await self.Database.StringSetAsync(key, serialized, expiration, When.NotExists, CommandFlags.DemandMaster).ConfigureAwait(false))
        {
            return new FillRaw(true, RedisValue.Null, DateTimeOffset.MaxValue);
        }

        return await ReadWinnerAsync(self, key).ConfigureAwait(false);
    }

    private ValueTask<FillResult<T>> TryFillCoreAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset deadline, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var format = FormatFor(policy, forWrite: true, alsoReads: true);
        return format.IsDefault
            ? TryFillInternalAsync(cacheKey, redisKey, value, deadline, default(PlainFormat), token)
            : TryFillInternalAsync(cacheKey, redisKey, value, deadline, format, token);
    }

    private ValueTask<ChangeResult<T>> SetIfChangedCoreAsync<T>(CacheKey cacheKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset deadline, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        ArgumentNullException.ThrowIfNull(comparer);
        var redisKey = ToRedisKey(cacheKey, token);
        var format = FormatFor(policy, forWrite: true, alsoReads: true);
        return format.IsDefault
            ? SetIfChangedInternalAsync(cacheKey, redisKey, value, comparer, deadline, default(PlainFormat), token)
            : SetIfChangedInternalAsync(cacheKey, redisKey, value, comparer, deadline, format, token);
    }

    [AsyncMethodBuilder(typeof(Pooled<>))]
    private async ValueTask<FillResult<T>> TryFillInternalAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, T? value, DateTimeOffset deadline, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return default;
        }

        var operation = StartOperation<T>(nameof(TryFillAsync));
        FillResult<T> result = default;
        try
        {
            var isNull = IsDefault(value);
            if (!CanStore(cacheKey, redisKey, isNull, typeof(T)))
            {
                operation.Stop();
                return default;
            }

            NoteWritten(redisKey);
            var (serialized, payload) = isNull ? (RedisValue.EmptyString, default(SerializedPayload)) : SerializeValue(value, format);
            _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), serialized);
            using (payload)
            {
                // Scripts off: one plain NX, which a win answers as a write does. Null is a command the pipeline gave up on, which is not a loss.
                var plain = !RewriteScriptsOn;
                bool? won = plain
                    ? await _write.ExecuteAsync(
                        [AsyncMethodBuilder(typeof(Pooled<>))] static async ValueTask<bool?> (s, token) =>
                        {
                            token.ThrowIfCancellationRequested();
                            var remaining = s.Self.RemainingOrZero(s.Deadline);
                            return remaining > TimeSpan.Zero && await s.Self.Database.StringSetAsync(s.Key, s.Serialized, remaining, When.NotExists, CommandFlags.DemandMaster).ConfigureAwait(false);
                        },
                        (Self: this, Key: redisKey, Serialized: serialized, Deadline: deadline),
                        null,
                        token).ConfigureAwait(false)
                    : false;

                if (won == true)
                {
                    result = new(FillOutcome.Added, default, deadline);
                }
                else if (won is not null)
                {
                    result = await FillAfterNxAsync<T, TFormat>(cacheKey, redisKey, serialized, deadline, plain, format, token).ConfigureAwait(false);
                }
            }

            operation.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            operation.Stop();
            LogRedisCacheException(ex);
            result = default;
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(result.Outcome is not (FillOutcome.Failed or FillOutcome.Unreadable));
        }

        return result;
    }

    /// <summary>The fill when its plain NX lost, or when scripts are on and the script decides: the stored value is the answer.</summary>
    private async ValueTask<FillResult<T>> FillAfterNxAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, RedisValue serialized, DateTimeOffset deadline, bool lost, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var raw = await _write.ExecuteAsync(static (s, token) => FillAfterAsync(s.Self, s.Key, s.Serialized, s.Deadline, s.Lost, token),
        (Self: this, Key: redisKey, Serialized: serialized, Deadline: deadline, Lost: lost),
        null,
        token).ConfigureAwait(false);

        if (raw is { IsLapsed: true })
        {
            LogFillSkipped(Logged(cacheKey, redisKey, typeof(T)), ExpirationPassed);
            return default;
        }

        if (raw is not { } stored)
        {
            return default;
        }

        return stored.Added ? new(FillOutcome.Added, default, deadline) : ExistingEntry<T, TFormat>(redisKey, stored, format);
    }

    /// <summary>A stored value as a read sees it; one its serializer throws on is logged and unreadable.</summary>
    private (bool Found, T? Value) ReadStored<T, TFormat>(RedisKey redisKey, RedisValue stored, TFormat format)
        where TFormat : struct, IValueFormat
    {
        try
        {
            var (found, deserialized, _) = InterpretReadResult<T, TFormat>(redisKey, stored, format);
            return (found, deserialized);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            LogRedisCacheException(ex);
            return (false, default);
        }
    }

    private FillResult<T> ExistingEntry<T, TFormat>(RedisKey redisKey, FillRaw stored, TFormat format)
        where TFormat : struct, IValueFormat
    {
        var (found, deserialized) = ReadStored<T, TFormat>(redisKey, stored.Value, format);
        return found ? new(FillOutcome.Existed, deserialized, stored.Expiration) : new(FillOutcome.Unreadable, default);
    }

    private async ValueTask<ChangeResult<T>> SetIfChangedInternalAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset deadline, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return default;
        }

        var operation = StartOperation<T>(nameof(SetIfChangedAsync));
        ChangeResult<T> result = default;
        var run = new ChangeRun();
        try
        {
            var isNull = IsDefault(value);
            if (!CanStore(cacheKey, redisKey, isNull, typeof(T)))
            {
                operation.Stop();
                return default;
            }

            NoteWritten(redisKey);
            var (serialized, payload) = isNull ? (RedisValue.EmptyString, default(SerializedPayload)) : SerializeValue(value, format);
            _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), serialized);
            using (payload)
            {
                result = await ChangeWithRetriesAsync(cacheKey, redisKey, value, comparer, deadline, format, serialized, run, token).ConfigureAwait(false);
            }

            operation.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (!run.Comparing)
        {
            operation.Stop();
            LogRedisCacheException(ex);
            result = default;
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(result.Outcome != ChangeOutcome.Failed);
        }

        return result;
    }

    private async ValueTask<ChangeResult<T>> ChangeWithRetriesAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset deadline, TFormat format, RedisValue serialized, ChangeRun run, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        ChangeResult<T> result = default;
        var losses = 0;
        for (var attempt = 0; attempt < FillAttempts && result.Outcome == ChangeOutcome.Failed; attempt++)
        {
            var (step, attempted) = await ChangeAttemptAsync(redisKey, value, comparer, deadline, format, serialized, run, token).ConfigureAwait(false);
            if (step == ChangeStep.Stopped)
            {
                break;
            }

            if (step == ChangeStep.Lost)
            {
                losses++;
            }
            else
            {
                result = attempted;
            }
        }

        if (result.Outcome == ChangeOutcome.Failed && losses == FillAttempts)
        {
            result = await OverwriteAfterLossesAsync(redisKey, value, deadline, serialized, run, token).ConfigureAwait(false);
        }

        if (run.Lapsed)
        {
            LogFillSkipped(Logged(cacheKey, redisKey, typeof(T)), ExpirationPassed);
            result = default;
        }

        return result;
    }

    private async ValueTask<(ChangeStep Step, ChangeResult<T> Result)> ChangeAttemptAsync<T, TFormat>(RedisKey redisKey, T? value, IEqualityComparer<T> comparer, DateTimeOffset deadline, TFormat format, RedisValue serialized, ChangeRun run, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var read = await ReadSnapshotRawAsync(redisKey, token).ConfigureAwait(false);
        if (read is not { } snapshot)
        {
            return (ChangeStep.Stopped, default);
        }

        if (UnchangedAgainst(redisKey, snapshot, value, comparer, format, run) is { } unchanged)
        {
            return (ChangeStep.Settled, unchanged);
        }

        // An unreadable stored value is replaced like a different one, which is the only way it ever heals.
        var written = await _write.ExecuteAsync(static async (s, token) =>
        {
            token.ThrowIfCancellationRequested();
            var remaining = s.Self.RemainingOrZero(s.Deadline);
            if (remaining <= TimeSpan.Zero)
            {
                s.Run.Lapsed = true;
                return null;
            }

            return s.Absent
                ? await s.Self.StoreIfAbsentAsync(s.Key, s.Serialized, remaining).ConfigureAwait(false)
                : await s.Self.ReplaceIfUnchangedAsync(s.Key, s.Expected, s.Serialized, s.Deadline, s.Run).ConfigureAwait(false);
        },
        (Self: this, Key: redisKey, Serialized: serialized, Deadline: deadline, Absent: snapshot.Value.IsNull, Expected: snapshot.Value, Run: run),
        null,
        token).ConfigureAwait(false);
        if (written is null)
        {
            return (ChangeStep.Stopped, default);
        }

        if (written == false)
        {
            return (ChangeStep.Lost, default);
        }

        return (ChangeStep.Settled, new ChangeResult<T>(snapshot.Value.IsNull ? ChangeOutcome.Filled : ChangeOutcome.Replaced, value, deadline));
    }

    private ChangeResult<T>? UnchangedAgainst<T, TFormat>(RedisKey redisKey, (RedisValue Value, DateTimeOffset Expiration) snapshot, T? value, IEqualityComparer<T> comparer, TFormat format, ChangeRun run)
        where TFormat : struct, IValueFormat
    {
        if (snapshot.Value.IsNull)
        {
            return null;
        }

        var (found, deserialized) = ReadStored<T, TFormat>(redisKey, snapshot.Value, format);
        if (!found)
        {
            return null;
        }

        run.Comparing = true;
        var equal = ChangeComparison.AreEqual(comparer, deserialized, value);
        run.Comparing = false;
        return equal ? new ChangeResult<T>(ChangeOutcome.Unchanged, deserialized, snapshot.Expiration) : null;
    }

    private async ValueTask<bool?> StoreIfAbsentAsync(RedisKey key, RedisValue serialized, TimeSpan remaining) =>
        await Database.StringSetAsync(key, serialized, remaining, When.NotExists, CommandFlags.DemandMaster).ConfigureAwait(false);

    private async ValueTask<bool?> ReplaceIfUnchangedAsync(RedisKey key, RedisValue expected, RedisValue serialized, DateTimeOffset deadline, ChangeRun run)
    {
        var done = await FormatRewriteScripts.ReplaceStringAsync(Database, key, expected, serialized, () => RemainingOrZero(deadline), RewriteScriptsOn, _rewriteScriptRefused).ConfigureAwait(false);
        run.Lapsed |= done is null;
        return done;
    }

    private async ValueTask<ChangeResult<T>> OverwriteAfterLossesAsync<T>(RedisKey redisKey, T? value, DateTimeOffset deadline, RedisValue serialized, ChangeRun run, CancellationToken token)
    {
        var overwritten = await _write.ExecuteAsync(static async (s, token) =>
        {
            token.ThrowIfCancellationRequested();
            var remaining = s.Self.RemainingOrZero(s.Deadline);
            if (remaining <= TimeSpan.Zero)
            {
                s.Run.Lapsed = true;
                return null;
            }

            return (bool?)await s.Self.Database.StringSetAsync(s.Key, s.Serialized, remaining, When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
        },
        (Self: this, Key: redisKey, Serialized: serialized, Deadline: deadline, Run: run),
        null,
        token).ConfigureAwait(false);
        return overwritten == true ? new ChangeResult<T>(ChangeOutcome.Replaced, value, deadline) : default;
    }

    private async ValueTask<(RedisValue Value, DateTimeOffset Expiration)?> ReadSnapshotRawAsync(RedisKey redisKey, CancellationToken token)
    {
        var read = new StrongBox<StrongBox<(RedisValue Value, DateTimeOffset? Expiration)>?>();
        var committed = await _read.ExecuteAsync(static async (s, token) =>
        {
            token.ThrowIfCancellationRequested();
            await ReadOnceAsync(s.Self, s.Key, s.Read, atomic: true).ConfigureAwait(false);
            return true;
        },
        (Self: this, Key: redisKey, Read: read),
        false,
        token).ConfigureAwait(false);

        if (!committed || read.Value is not { } result)
        {
            return null;
        }

        var (value, expiration) = result.Value;
        return (value, Clock.ToDateTimeOffset(expiration));
    }

    /// <summary><see cref="TimeSpan.MaxValue"/> for no deadline.</summary>
    private TimeSpan RemainingOrZero(DateTimeOffset deadline) => Remaining(deadline) ?? TimeSpan.Zero;

    private bool CanStore(CacheKey cacheKey, RedisKey redisKey, bool isNull, Type valueType)
    {
        if (isNull && !_cacheNullValues)
        {
            LogFillSkipped(Logged(cacheKey, redisKey, valueType), "a default value cannot be represented unless CacheNullValues is on");
            return false;
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryFill or SetIfChanged skipped for {RedisKey}: {Reason}.")]
    private partial void LogFillSkipped(LoggedKey redisKey, string reason);

    private readonly record struct FillRaw(bool Added, RedisValue Value, DateTimeOffset Expiration, bool IsLapsed = false)
    {
        public static FillRaw Lapsed { get; } = new(false, RedisValue.Null, DateTimeOffset.MinValue, true);
    }

    private sealed class ChangeRun
    {
        public bool Comparing { get; set; }

        public bool Lapsed { get; set; }
    }
}
