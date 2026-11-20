using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache
{
    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(policy), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), token);

    public ValueTask<bool> RefreshAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        RefreshCoreAsync<T>(cacheKey, GetExpiration(expiration), token);

    public ValueTask<bool> RemoveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return RemoveAsync<T>(ToRedisKey(cacheKey, token), token);
    }

    public ValueTask<bool> RemoveAsync<T>(CacheKey[] cacheKey, CancellationToken token = default)
    {
        return RemoveAsync<T>(Array.ConvertAll(cacheKey, k => ToRedisKey(k, token)), token);
    }

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, PolicyDuration(policy), policy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, CallerDuration(expiration), policy, token);

    public ValueTask<bool> SetAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(cacheKey, value, CallerDuration(expiration), policy, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, PolicyDuration(policy), policy, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, CallerDuration(expiration), policy, token);

    public ValueTask<bool> SetAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        SetCoreAsync(keyValues, CallerDuration(expiration), policy, token);

    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, PolicyDuration(policy), policy, token);

    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, CallerDuration(expiration), policy, token);

    public ValueTask<bool> TryAddAsync<T>(CacheKey cacheKey, T? value, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default) =>
        TryAddCoreAsync(cacheKey, value, CallerDuration(expiration), policy, token);

    private async ValueTask<bool> RefreshCoreAsync<T>(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);

        LogRefreshingKey(Logged(cacheKey, redisKey, typeof(T)), expiration);
        var ret = false;
        NoteWritten(redisKey);
        var operation = StartOperation<T>();
        try
        {
            if (expiration == DateTimeOffset.MaxValue)
            {
                ret = await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyPersistAsync(s.Key, s.Self.RefreshFlags).AsValueTask();
                },
                (Self: this, Key: redisKey),
                default,
                token).ConfigureAwait(false);
            }
            else
            {
                ret = await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyExpireAsync(s.Key, s.Expiration.UtcDateTime, s.Self.RefreshFlags).AsValueTask();
                },
                (Self: this, Key: redisKey, Expiration: expiration),
                default,
                token).ConfigureAwait(false);
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

        return ret;
    }

    private ValueTask<bool> SetCoreAsync<T>(CacheKey cacheKey, T? value, TimeSpan duration, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var format = FormatFor(policy, forWrite: true);
        return format.IsDefault
            ? SetInternalAsync(cacheKey, redisKey, value, duration, default(PlainFormat), token)
            : SetInternalAsync(cacheKey, redisKey, value, duration, format, token);
    }

    private ValueTask<bool> SetCoreAsync<T>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan duration, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: true);
        return format.IsDefault
            ? SetInternalAsync(keyValues, duration, default(PlainFormat), token)
            : SetInternalAsync(keyValues, duration, format, token);
    }

    private ValueTask<bool> TryAddCoreAsync<T>(CacheKey cacheKey, T? value, TimeSpan duration, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var format = FormatFor(policy, forWrite: true);
        return format.IsDefault
            ? TryAddInternalAsync(cacheKey, redisKey, value, duration, default(PlainFormat), token)
            : TryAddInternalAsync(cacheKey, redisKey, value, duration, format, token);
    }

    /// <summary>Borrowed memory is safe here because every write awaits its command and the connection copies the value while writing it; pooled memory goes back only then.</summary>
    private (RedisValue Value, SerializedPayload Payload) SerializeValue<T, TFormat>(T? value, TFormat format)
        where TFormat : struct, IValueFormat
    {
        if (format.Writer is { } writer)
        {
            if (SerializerMetricsOn)
            {
                TrackSerializer(Metrics.SerializerWrite, _valueSerializers.NameOf(writer), Name);
            }

            var headered = SerializedPayload.Serialize(writer, _valueSerializers.FormatIdsOf(writer).Span, value!);
            return (headered.Memory, headered);
        }

        TrackJsonWrite(format);
        if (_memorySerializer is { } memory)
        {
            var payload = SerializedPayload.Serialize(memory, value);
            return (payload.Memory, payload);
        }
        return (_serializer.Serialize(value), default);
    }

    private async ValueTask<bool> SetInternalAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, T? value, TimeSpan expiration, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return false;
        }

        NoteWritten(redisKey);
        var operation = StartOperation<T>(nameof(SetAsync));
        try
        {
            if (IsDefault(value))
            {
                if (_cacheNullValues && expiration > TimeSpan.Zero)
                {
                    ret = await _write.ExecuteAsync(static (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return s.Self.Database.StringSetAsync(s.Key, RedisValue.EmptyString, s.Expiration, When.Always, CommandFlags.DemandMaster).AsValueTask();
                    },
                    (Self: this, Key: redisKey, Expiration: expiration),
                    default,
                    token).ConfigureAwait(false);
                }
                else
                {
                    await RemoveAsync<T>(redisKey, token).ConfigureAwait(false);
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
                        return s.Self.Database.StringSetAsync(s.Key, s.Serialized, s.Expiration, When.Always, CommandFlags.DemandMaster).AsValueTask();
                    },
                    (Self: this, Key: redisKey, Serialized: serialized, Expiration: expiration),
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

        return ret;
    }

    private async ValueTask<bool> SetInternalAsync<T, TFormat>(KeyValuePair<CacheKey, T?>[] keyValues, TimeSpan expiration, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();
        var redisKeys = Array.ConvertAll(keyValues, kv => ToRedisKey(kv.Key, token));
        NoteWritten(redisKeys);

        if (!IsConnected)
        {
            return false;
        }

        var operation = StartOperation<T>(nameof(SetAsync));
        var payloads = new SerializedPayload[keyValues.Length];
        try
        {
            await EnsureConnectedAsync(_write, token).ConfigureAwait(false);
            var committed = await PerSlotAsync(
                SlotGroups(redisKeys, _logger),
                (Self: this, KeyValues: keyValues, Keys: redisKeys, Payloads: payloads, Expiration: expiration, Format: format, Token: token),
                static (s, group) => s.Self.SetSlotAsync(s.KeyValues, s.Keys, s.Payloads, group, s.Expiration, s.Format, s.Token),
                false,
                LogRedisCacheException).ConfigureAwait(false);
            ret = Array.TrueForAll(committed, static c => c);
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
            foreach (var payload in payloads)
            {
                payload.Dispose();
            }
            NoteWritten(redisKeys);
            operation.Track(ret, keyValues.Length);
        }

        return ret;
    }

    /// <summary>One transaction for the keys of one slot; it serializes into <paramref name="payloads"/>, which the caller disposes.</summary>
    private ValueTask<bool> SetSlotAsync<T, TFormat>(KeyValuePair<CacheKey, T?>[] keyValues, RedisKey[] redisKeys, SerializedPayload[] payloads, int[] group, TimeSpan expiration, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var transaction = Database.CreateTransaction(asyncState: null);
        foreach (var i in group)
        {
            var redisKey = redisKeys[i];
            var value = keyValues[i].Value;
            if (IsDefault(value))
            {
                if (_cacheNullValues && expiration > TimeSpan.Zero)
                {
                    transaction.StringSetAsync(redisKey, RedisValue.EmptyString, expiration, When.Always, CommandFlags.DemandMaster).Forget();
                }
                else
                {
                    transaction.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster).Forget();
                }
            }
            else
            {
                (var serialized, payloads[i]) = SerializeValue(value, format);
                _auditKeySize?.Invoke(Logged(keyValues[i].Key, redisKey, typeof(T)), serialized);
                transaction.StringSetAsync(redisKey, serialized, expiration, When.Always, CommandFlags.DemandMaster).Forget();
            }
        }

        return _write.ExecuteAsync(static (s, token) =>
        {
            token.ThrowIfCancellationRequested();
            return s.ExecuteAsync(CommandFlags.DemandMaster).AsValueTask();
        },
        transaction,
        default,
        token);
    }

    /// <summary>
    /// <c>SET key value EX … NX</c> in one round-trip: Redis decides, so no probe precedes the write.
    /// Safe to retry — an attempt whose reply was lost is refused by the key it just wrote, and
    /// reports the same <c>false</c> the exception would have.
    /// </summary>
    private async ValueTask<bool> TryAddInternalAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, T? value, TimeSpan expiration, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return false;
        }

        var operation = StartOperation<T>(nameof(TryAddAsync));
        try
        {
            var isNull = IsDefault(value);
            if (expiration <= TimeSpan.Zero)
            {
                LogTryAddSkippedExpiredEntry(Logged(cacheKey, redisKey, typeof(T)), expiration);
            }
            else if (isNull && !_cacheNullValues)
            {
                LogTryAddSkippedUnrepresentableValue(Logged(cacheKey, redisKey, typeof(T)));
            }
            else
            {
                NoteWritten(redisKey);
                var (serialized, payload) = isNull ? (RedisValue.EmptyString, default(SerializedPayload)) : SerializeValue(value, format);
                _auditKeySize?.Invoke(Logged(cacheKey, redisKey, typeof(T)), serialized);
                using (payload)
                {
                    ret = await _write.ExecuteAsync(static (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return s.Self.Database.StringSetAsync(s.Key, s.Serialized, s.Expiration, When.NotExists, CommandFlags.DemandMaster).AsValueTask();
                    },
                    (Self: this, Key: redisKey, Serialized: serialized, Expiration: expiration),
                    default,
                    token).ConfigureAwait(false);
                }
            }
            operation.Stop();
        }
        // false would claim someone else owns the key, which a cancelled call never established.
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

        return ret;
    }

    private async ValueTask<bool> RemoveAsync<T>(RedisKey redisKey, CancellationToken token)
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();
        NoteWritten(redisKey);
        var operation = StartOperation<T>();
        try
        {
            ret = await _write.ExecuteAsync(static async (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                await s.Self.Database.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster).ConfigureAwait(false);
                return true;
            },
            (Self: this, Key: redisKey),
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

        return ret;
    }

    private async ValueTask<bool> RemoveAsync<T>(RedisKey[] redisKey, CancellationToken token)
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();
        NoteWritten(redisKey);
        var operation = StartOperation<T>();
        try
        {
            await EnsureConnectedAsync(_write, token).ConfigureAwait(false);
            var responses = await PerSlotAsync(
                SlotGroups(redisKey, _logger),
                (Self: this, Keys: redisKey, Token: token),
                static (s, group) => s.Self._write.ExecuteAsync(static (r, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return r.Self.Database.KeyDeleteAsync(r.Keys, CommandFlags.DemandMaster).AsValueTask();
                },
                (s.Self, Keys: Pick(s.Keys, group)),
                -1L,
                s.Token),
                -1L,
                LogRedisCacheException).ConfigureAwait(false);
            operation.Stop();
            ret = Array.TrueForAll(responses, static r => r > -1);
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
            operation.Track(ret, redisKey.Length);
        }

        return ret;
    }
}
