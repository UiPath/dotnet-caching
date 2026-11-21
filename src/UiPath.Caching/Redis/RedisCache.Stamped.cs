using System.Runtime.CompilerServices;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache : IStampedTier
{
    public ValueTask<(SnapshotRead<T> Snapshot, object? Stamp)> ReadStampedAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff
            ? ReadStampedCoreAsync<T, ValueFormat>(cacheKey, format, token)
            : ReadStampedCoreAsync<T, PlainFormat>(cacheKey, default, token);
    }

    public ValueTask<StampedWrite> WriteIfStampAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, object? stamp, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var format = FormatFor(policy, forWrite: true, alsoReads: true);
        var expected = stamp is RedisValue seen ? seen : RedisValue.Null;
        return format.IsDefault
            ? WriteIfStampCoreAsync(cacheKey, redisKey, value, expiration, expected, default(PlainFormat), token)
            : WriteIfStampCoreAsync(cacheKey, redisKey, value, expiration, expected, format, token);
    }

    private async ValueTask<(SnapshotRead<T> Snapshot, object? Stamp)> ReadStampedCoreAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var raw = new StrongBox<RedisValue>();
        var snapshot = await ReadSnapshotInternalAsync<T, TFormat>(cacheKey, format, token, raw).ConfigureAwait(false);
        return (snapshot, snapshot.Outcome is SnapshotOutcome.Found or SnapshotOutcome.Unreadable ? (object)raw.Value : null);
    }

    private async ValueTask<StampedWrite> WriteIfStampCoreAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, T? value, DateTimeOffset deadline, RedisValue expected, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            return default;
        }

        var operation = StartOperation<T>(nameof(WriteIfStampAsync));
        var result = StampedWrite.Failed;
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
                var written = await _write.ExecuteAsync(static async (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var remaining = s.Self.RemainingOrZero(s.Deadline);
                    if (remaining <= TimeSpan.Zero)
                    {
                        return null;
                    }

                    return s.Expected.IsNull
                        ? await s.Self.StoreIfAbsentAsync(s.Key, s.Serialized, remaining).ConfigureAwait(false)
                        : await s.Self.ReplaceIfUnchangedAsync(s.Key, s.Expected, s.Serialized, s.Deadline, new ChangeRun()).ConfigureAwait(false);
                },
                (Self: this, Key: redisKey, Serialized: serialized, Deadline: deadline, Expected: expected),
                null,
                token).ConfigureAwait(false);
                result = written switch
                {
                    true => StampedWrite.Won,
                    false => StampedWrite.Lost,
                    _ => StampedWrite.Failed,
                };
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
            result = StampedWrite.Failed;
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(result != StampedWrite.Failed);
        }

        return result;
    }
}
