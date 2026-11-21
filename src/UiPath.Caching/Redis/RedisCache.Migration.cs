using StackExchange.Redis;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache
{
    public async ValueTask<StoreOutcome> WriteAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        RedisKey redisKey;
        ValueFormat format;
        TimeSpan duration;
        try
        {
            NotCacheableException.ThrowIfNotCacheable<T>();
            if (token.IsCancellationRequested || !IsConnected || !TryToRedisKey(cacheKey, token, out redisKey))
            {
                return StoreOutcome.NotSent;
            }

            format = FormatFor(policy, forWrite: true);
            duration = CallerDuration(expiration);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRedisCacheException(ex);
            return StoreOutcome.NotSent;
        }

        var trace = new SendTrace();
        try
        {
            if (format.IsDefault)
            {
                await SendSetAsync(cacheKey, redisKey, value, duration, default(PlainFormat), trace, token).ConfigureAwait(false);
            }
            else
            {
                await SendSetAsync(cacheKey, redisKey, value, duration, format, trace, token).ConfigureAwait(false);
            }

            return trace.Outcome();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return trace.Outcome();
        }
    }

    public async ValueTask<StoreOutcome> DeleteAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        RedisKey redisKey;
        try
        {
            NotCacheableException.ThrowIfNotCacheable<T>();
            if (token.IsCancellationRequested || !IsConnected || !TryToRedisKey(cacheKey, token, out redisKey))
            {
                return StoreOutcome.NotSent;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRedisCacheException(ex);
            return StoreOutcome.NotSent;
        }

        var trace = new SendTrace();
        try
        {
            await SendDeleteAsync<T>(redisKey, trace, token).ConfigureAwait(false);
            return trace.Outcome();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return trace.Outcome();
        }
    }

    /// <summary>The write of <c>SetAsync</c>, with the trace owned by this one command through every retry.</summary>
    private async ValueTask SendSetAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, T? value, TimeSpan expiration, TFormat format, SendTrace trace, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        NoteWritten(redisKey);
        var operation = StartOperation<T>(nameof(SetAsync));
        var ret = false;
        try
        {
            if (IsDefault(value))
            {
                if (_cacheNullValues && expiration > TimeSpan.Zero)
                {
                    ret = await _write.ExecuteAsync(static (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        s.Trace.MarkSent();
                        return s.Trace.AcknowledgeAsync(s.Self.Database.StringSetAsync(s.Key, RedisValue.EmptyString, s.Expiration, When.Always, CommandFlags.DemandMaster));
                    },
                    (Self: this, Key: redisKey, Expiration: expiration, Trace: trace),
                    default,
                    token).ConfigureAwait(false);
                }
                else
                {
                    await SendDeleteAsync<T>(redisKey, trace, token).ConfigureAwait(false);
                    ret = true;
                }
            }
            else
            {
                var (serialized, payload) = SerializeValue(value, format);
                _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), serialized);
                using (payload)
                {
                    ret = await _write.ExecuteAsync(static (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        s.Trace.MarkSent();
                        return s.Trace.AcknowledgeAsync(s.Self.Database.StringSetAsync(s.Key, s.Serialized, s.Expiration, When.Always, CommandFlags.DemandMaster));
                    },
                    (Self: this, Key: redisKey, Serialized: serialized, Expiration: expiration, Trace: trace),
                    default,
                    token).ConfigureAwait(false);
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
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(ret);
        }
    }

    /// <summary>The delete of <c>RemoveAsync</c>, with the trace owned by this one command through every retry.</summary>
    private async ValueTask SendDeleteAsync<T>(RedisKey redisKey, SendTrace trace, CancellationToken token)
    {
        var ret = false;
        token.ThrowIfCancellationRequested();
        NoteWritten(redisKey);
        var operation = StartOperation<T>();
        try
        {
            ret = await _write.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                s.Trace.MarkSent();
                return s.Trace.AcknowledgeRanAsync(s.Self.Database.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster));
            },
            (Self: this, Key: redisKey, Trace: trace),
            default,
            token).ConfigureAwait(false);
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
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(ret);
        }
    }
}
