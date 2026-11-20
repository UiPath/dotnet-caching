using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache
{
    [OverloadResolutionPriority(1)]
    public ValueTask<T?> GetAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        var redisKey = ToRedisKey(cacheKey, token);
        return format.Sniff ? GetAsync<T, ValueFormat>(redisKey, format, token) : GetAsync<T, PlainFormat>(redisKey, default, token);
    }

    public ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetAsyncInternal<T, ValueFormat>(cacheKeys, format, token) : GetAsyncInternal<T, PlainFormat>(cacheKeys, default, token);
    }

    public ValueTask<ICacheEntry<T?>> GetCacheEntryAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetCacheEntryInternalAsync<T, ValueFormat>(cacheKey, format, token) : GetCacheEntryInternalAsync<T, PlainFormat>(cacheKey, default, token);
    }

    public ValueTask<SnapshotRead<T>> ReadSnapshotAsync<T>(CacheKey cacheKey, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff
            ? ReadSnapshotInternalAsync<T, ValueFormat>(cacheKey, format, token)
            : ReadSnapshotInternalAsync<T, PlainFormat>(cacheKey, default, token);
    }

    public ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesAsync<T>(CacheKey[] cacheKeys, CachePolicy? policy, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: false);
        return format.Sniff ? GetCacheEntriesInternalAsync<T, ValueFormat>(cacheKeys, format, token) : GetCacheEntriesInternalAsync<T, PlainFormat>(cacheKeys, default, token);
    }

    public ValueTask<bool> ContainsAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        return RunAsync(_read, StartOperation<T>(), static (s, token) => s.Self.Database.KeyExistsAsync(s.Key, s.Self.ReadFlagsFor(s.Key)).AsValueTask(), (Self: this, Key: redisKey), false, static hit => hit, _logFailure, token);
    }

    public ValueTask<TimeSpan?> TimeToLiveAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return KeyTimeToLiveAsync(_read, StartOperation<T>(), _redisKeyStrategy, cacheKey, _logFailure, token);
    }

    public ValueTask<DateTimeOffset?> ExpireTimeAsync<T>(CacheKey cacheKey, CancellationToken token = default)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        return KeyExpireTimeAsync(_read, StartOperation<T>(), _redisKeyStrategy, cacheKey, _logFailure, token);
    }

    private static (RedisKey Key, bool Hit)[] InitReads(RedisKey[] redisKeys)
    {
        var reads = new (RedisKey Key, bool Hit)[redisKeys.Length];
        for (int i = 0; i < redisKeys.Length; i++)
        {
            reads[i] = (redisKeys[i], false);
        }
        return reads;
    }

    private static (RedisValue Value, DateTimeOffset? Expiration) ReadReply(RedisResult reply)
    {
        if (reply.IsNull)
        {
            return (RedisValue.Null, null);
        }

        var parts = ((RedisResult[]?)reply)!;
        var expireAt = (long)parts[1];
        // PEXPIRETIME answers -1 for a key without a TTL, as EXPIRETIME does for KeyExpireTimeAsync.
        return ((RedisValue)parts[0], expireAt < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(expireAt));
    }

    private static (RedisValue[] Values, DateTimeOffset?[] Expirations) ReadManyReply(RedisResult reply, int count)
    {
        var parts = ((RedisResult[]?)reply)!;
        var values = new RedisValue[count];
        var expirations = new DateTimeOffset?[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = (RedisValue)parts[2 * i];
            var expireAt = (long)parts[(2 * i) + 1];
            expirations[i] = expireAt < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(expireAt);
        }

        return (values, expirations);
    }

    /// <summary>One attempt at the read: the snapshot transaction, the script, or a GET with a PTTL.</summary>
    private static async Task ReadOnceAsync(RedisCache self, RedisKey key, StrongBox<StrongBox<(RedisValue Value, DateTimeOffset? Expiration)>?> read, bool atomic)
    {
        if (atomic)
        {
            var transaction = self.Database.CreateTransaction();
            var snapshotValue = transaction.StringGetAsync(key, CommandFlags.DemandMaster);
            var snapshotTimeToLive = transaction.KeyTimeToLiveAsync(key, CommandFlags.DemandMaster);
            if (!await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The read transaction was not committed");
            }

            Interlocked.CompareExchange(ref read.Value, new((await snapshotValue.ConfigureAwait(false), self.Clock.ToDateTimeOffset(await snapshotTimeToLive.ConfigureAwait(false)))), null);
            return;
        }

        if (await self.TryReadScriptAsync(ReadWithExpirationScript, [key], self._logReadScriptUnavailable).ConfigureAwait(false) is { } reply)
        {
            Interlocked.CompareExchange(ref read.Value, new(ReadReply(reply)), null);
            return;
        }

        // A GET with a PTTL in one round trip. Not atomic: a key that expires between them reads as a value without a TTL.
        var flags = self.ReadFlagsFor(key);
        var valueTask = self.Database.StringGetAsync(key, flags);
        var timeToLiveTask = self.Database.KeyTimeToLiveAsync(key, flags);
        try
        {
            var value = await valueTask.ConfigureAwait(false);
            var expiration = self.Clock.ToDateTimeOffset(await timeToLiveTask.ConfigureAwait(false));
            Interlocked.CompareExchange(ref read.Value, new((value, expiration)), null);
        }
        catch
        {
            valueTask.Observe();
            timeToLiveTask.Observe();
            throw;
        }
    }

    /// <summary>One attempt at a batch read: the script, or an MGET with a PTTL per key.</summary>
    private static async ValueTask<bool> ReadManyAsync((RedisCache Self, RedisKey[] Keys, StrongBox<StrongBox<(RedisValue[] Values, DateTimeOffset?[] Expirations)>?> Read) s, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (await s.Self.TryReadScriptAsync(ReadManyWithExpirationScript, s.Keys, s.Self._logReadScriptUnavailable).ConfigureAwait(false) is { } reply)
        {
            Interlocked.CompareExchange(ref s.Read.Value, new(ReadManyReply(reply, s.Keys.Length)), null);
            return true;
        }

        // An MGET with a PTTL per key in one round trip; not atomic, see GetCacheEntryInternalAsync.
        var flags = s.Self.ReadFlagsFor(s.Keys);
        var mgetTask = s.Self.Database.StringGetAsync(s.Keys, flags);
        var expirationTasks = new Task<TimeSpan?>[s.Keys.Length];
        for (var i = 0; i < expirationTasks.Length; i++)
        {
            expirationTasks[i] = s.Self.Database.KeyTimeToLiveAsync(s.Keys[i], flags);
        }

        try
        {
            var values = await mgetTask.ConfigureAwait(false);
            var timesToLive = await Task.WhenAll(expirationTasks).ConfigureAwait(false);
            var clock = s.Self.Clock;
            Interlocked.CompareExchange(ref s.Read.Value, new((values, Array.ConvertAll(timesToLive, t => (DateTimeOffset?)clock.ToDateTimeOffset(t)))), null);
        }
        catch
        {
            mgetTask.Observe();
            Array.ForEach(expirationTasks, TaskObservation.Observe);
            throw;
        }

        return true;
    }

    private static KeyValuePair<CacheKey, T?>[] GetDefaultValues<T>(CacheKey[] keys) =>
        [.. keys.Select(k => new KeyValuePair<CacheKey, T?>(k, default))];

    /// <summary>
    /// Tri-state read: <c>IsNull</c> -> miss; <c>Length == 0</c> -> cached-null hit (only honored when
    /// <c>CacheNullValues</c> is on); non-empty -> deserialize. Legacy round-trip to <c>default(T)</c>
    /// is also treated as a miss when the option is off.
    /// </summary>
    private (bool Found, T? Value, int Unreadable) InterpretReadResult<T, TFormat>(RedisKey key, RedisValue value, TFormat format)
        where TFormat : struct, IValueFormat =>
        InterpretReadResult<T, TFormat>(key, value, format, out _);

    /// <param name="undecoded">Stored bytes were present but gave no value.</param>
    private (bool Found, T? Value, int Unreadable) InterpretReadResult<T, TFormat>(RedisKey key, RedisValue value, TFormat format, out bool undecoded)
        where TFormat : struct, IValueFormat
    {
        undecoded = false;
        if (value.IsNull)
        {
            return (false, default, 0);
        }

        T? deserialized;
        byte[]? stored = null;
        if (value.Length() == 0)
        {
            deserialized = default;
        }
        else if (format.Sniff)
        {
            stored = (byte[]?)value;
            var outcome = _formats.Reader!.Read<T>(stored!, format.Readable);
            if (outcome.IsMiss)
            {
                ReportReadMiss(outcome);
                undecoded = true;
                return (false, default, outcome.UnreadableFormat);
            }

            deserialized = outcome.Value;
            if (SerializerMetricsOn)
            {
                TrackSerializer(Metrics.SerializerRead, _formats.Reader.NameOf(outcome.SourceFormat), Name);
            }
        }
        else
        {
            deserialized = _serializer.Deserialize<T>(value);
        }

        undecoded = deserialized is null && value.Length() != 0;
        if (!_cacheNullValues && IsDefault(deserialized))
        {
            return (false, default, 0);
        }

        if (format.Sniff && stored is not null && format.Readable!.MigrateTo is { } target && Migration is { } migration && !IsDefault(deserialized) && migration.Wants(target, stored))
        {
            OfferRewrite<T>(key, stored, target, migration);
        }

        return (true, deserialized, 0);
    }

    private void ReportReadMiss<T>(ReadOutcome<T> outcome)
    {
        if (outcome.Failure is { } failure)
        {
            LogRedisCacheException(failure);
        }
        else if (outcome.ExcludedFormat != 0)
        {
            TrackUnreadable(outcome.ExcludedFormat, Name);
        }
    }

    /// <summary>As <see cref="InterpretReadResult{T, TFormat}"/>, for one value of a batch: a format no serializer owns joins <paramref name="unreadableKeys"/>, which the batch drops after it has reported.</summary>
    private (bool Found, T? Value) InterpretBatchRead<T, TFormat>(RedisKey redisKey, RedisValue value, TFormat format, ref List<(RedisKey Key, RedisValue Read, int FormatId)>? unreadableKeys)
        where TFormat : struct, IValueFormat
    {
        var (found, deserialized, unreadable) = InterpretReadResult<T, TFormat>(redisKey, value, format);
        if (unreadable != 0)
        {
            (unreadableKeys ??= []).Add((redisKey, value, unreadable));
        }

        return (found, deserialized);
    }

    private ValueTask<RedisValue> ReadStringAsync(RedisKey redisKey, CancellationToken token) =>
        _read.ExecuteAsync(static (s, token) =>
        {
            token.ThrowIfCancellationRequested();
            return s.Self.Database.StringGetAsync(s.Key, s.Self.ReadFlagsFor(s.Key)).AsValueTask();
        },
        (Self: this, Key: redisKey),
        RedisValue.Null,
        token);

    // GenerateAndStoreAsync carries an inlined copy of this read; a change to one goes in both.
    private async ValueTask<(bool Found, T? Value)> ReadGetOrAddProbeAsync<T, TFormat>(RedisKey redisKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return (false, default);
        }

        var operation = StartOperation<T>(nameof(GetOrAddAsync));
        bool found = false;
        T? deserialized = default;
        try
        {
            var value = await ReadStringAsync(redisKey, token).ConfigureAwait(false);
            (found, deserialized, var unreadable) = InterpretReadResult<T, TFormat>(redisKey, value, format);
            if (unreadable != 0)
            {
                await RemoveUnreadableAsync<T>(redisKey, value, unreadable, token).ConfigureAwait(false);
            }

            operation.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return (found, deserialized);
    }

    private async ValueTask<T?> GetAsync<T, TFormat>(RedisKey redisKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        T? ret = default;
        bool found = false;
        token.ThrowIfCancellationRequested();

        if (!IsConnected)
        {
            return default;
        }

        var operation = StartOperation<T>(nameof(GetAsync));

        try
        {
            var value = await ReadStringAsync(redisKey, token).ConfigureAwait(false);
            (found, ret, var unreadable) = InterpretReadResult<T, TFormat>(redisKey, value, format);
            if (unreadable != 0)
            {
                await RemoveUnreadableAsync<T>(redisKey, value, unreadable, token).ConfigureAwait(false);
            }

            operation.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }

        return ret;
    }

    private async ValueTask<KeyValuePair<CacheKey, T?>[]> GetAsyncInternal<T, TFormat>(CacheKey[] keys, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        KeyValuePair<CacheKey, T?>[] retValues = new KeyValuePair<CacheKey, T?>[keys.Length];
        token.ThrowIfCancellationRequested();
        if (keys.Length == 0)
        {
            return retValues;
        }
        var redisKeys = Array.ConvertAll(keys, k => ToRedisKey(k, token));
        if (!IsConnected)
        {
            return GetDefaultValues<T>(keys);
        }
        var operation = StartOperation<T>(nameof(GetAsyncInternal));
        var reads = InitReads(redisKeys);
        List<(RedisKey Key, RedisValue Read, int FormatId)>? unreadableKeys = null;
        try
        {
            await EnsureConnectedAsync(_read, token).ConfigureAwait(false);
            var groups = SlotGroups(redisKeys, _logger);
            var groupValues = await PerSlotAsync(
                groups,
                (Self: this, Keys: redisKeys, Token: token),
                static (s, group) => s.Self._read.ExecuteAsync(static (r, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return r.Self.Database.StringGetAsync(r.Keys, r.Self.ReadFlagsFor(r.Keys)).AsValueTask();
                },
                (s.Self, Keys: Pick(s.Keys, group)),
                [],
                s.Token),
                [],
                LogRedisCacheException).ConfigureAwait(false);
            retValues = GetDefaultValues<T>(keys);
            for (var g = 0; g < groups.Length; g++)
            {
                var (group, values) = (groups[g], groupValues[g]);
                // A slot whose read answered nothing reads as misses; the others keep their values.
                if (values.Length != group.Length)
                {
                    continue;
                }

                for (var j = 0; j < group.Length; j++)
                {
                    var i = group[j];
                    var (found, obj) = InterpretBatchRead<T, TFormat>(redisKeys[i], values[j], format, ref unreadableKeys);
                    reads[i].Hit = found;
                    retValues[i] = new KeyValuePair<CacheKey, T?>(keys[i], obj);
                }
            }
            operation.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisCacheException(ex);
            retValues = GetDefaultValues<T>(keys);
            reads = InitReads(redisKeys);
        }
        finally
        {
            TrackPerKey(operation, reads);
        }

        if (unreadableKeys is not null)
        {
            await RemoveUnreadableAsync<T, TFormat>(unreadableKeys, format, token).ConfigureAwait(false);
        }

        return retValues;
    }

    private async ValueTask<ICacheEntry<T?>> GetCacheEntryInternalAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token, string methodName = nameof(GetCacheEntryInternalAsync))
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();
        var redisKey = ToRedisKey(cacheKey, token);
        if (!IsConnected)
        {
            return DefaultEntry<T>();
        }

        var operation = StartOperation<T>(methodName);
        ICacheEntry<T?> ret = DefaultEntry<T>();
        try
        {
            // Per attempt; boxed so the pipeline stays bool.
            var read = new StrongBox<StrongBox<(RedisValue Value, DateTimeOffset? Expiration)>?>();
            var committed = await _read.ExecuteAsync(static async (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                await ReadOnceAsync(s.Self, s.Key, s.Read, atomic: false).ConfigureAwait(false);
                return true;
            },
            (Self: this, Key: redisKey, Read: read),
            default,
            token).ConfigureAwait(false);

            if (!committed || read.Value is not { } result)
            {
                operation.Stop();
                return ret;
            }

            var (value, expiration) = result.Value;
            var (found, deserialized, unreadable) = InterpretReadResult<T, TFormat>(redisKey, value, format);
            if (found)
            {
                ret = _cacheEntryFactory.Create<T?>(deserialized, Clock.ToDateTimeOffset(expiration));
            }

            if (unreadable != 0)
            {
                await RemoveUnreadableAsync<T>(redisKey, value, unreadable, token).ConfigureAwait(false);
            }

            operation.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisCacheException(ex);
        }
        finally
        {
            TrackRead(operation, ret.Found, redisKey);
        }

        return ret;
    }

    private async ValueTask<SnapshotRead<T>> ReadSnapshotInternalAsync<T, TFormat>(CacheKey cacheKey, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            return default;
        }

        RedisKey redisKey;
        try
        {
            redisKey = ToRedisKey(cacheKey, token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !(ex is OperationCanceledException && token.IsCancellationRequested))
        {
            LogRedisCacheException(ex);
            return default;
        }

        var operation = StartOperation<T>(nameof(ReadSnapshotAsync));
        var found = false;
        try
        {
            var read = new StrongBox<StrongBox<(RedisValue Value, DateTimeOffset? Expiration)>?>();
            var committed = await _read.ExecuteAsync(static async (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                await ReadOnceAsync(s.Self, s.Key, s.Read, atomic: true).ConfigureAwait(false);
                return true;
            },
            (Self: this, Key: redisKey, Read: read),
            default,
            token).ConfigureAwait(false);

            if (!committed || read.Value is not { } result)
            {
                operation.Stop();
                return default;
            }

            var (value, expiration) = result.Value;
            var snapshot = InterpretSnapshot<T, TFormat>(redisKey, value, Clock.ToDateTimeOffset(expiration), format);
            found = snapshot.Outcome == SnapshotOutcome.Found;
            operation.Stop();
            return snapshot;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            operation.Stop();
            LogRedisCacheException(ex);
            return default;
        }
        finally
        {
            TrackRead(operation, found, redisKey);
        }
    }

    private SnapshotRead<T> InterpretSnapshot<T, TFormat>(RedisKey key, RedisValue value, DateTimeOffset expiration, TFormat format)
        where TFormat : struct, IValueFormat
    {
        bool found;
        T? deserialized;
        bool undecoded;
        try
        {
            (found, deserialized, _) = InterpretReadResult<T, TFormat>(key, value, format, out undecoded);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            LogRedisCacheException(ex);
            return new SnapshotRead<T>(SnapshotOutcome.Unreadable, null);
        }

        if (undecoded)
        {
            return new SnapshotRead<T>(SnapshotOutcome.Unreadable, null);
        }

        return found
            ? new SnapshotRead<T>(SnapshotOutcome.Found, _cacheEntryFactory.Create<T?>(deserialized, expiration))
            : new SnapshotRead<T>(SnapshotOutcome.Absent, null);
    }

    private async ValueTask<KeyValuePair<CacheKey, ICacheEntry<T?>>[]> GetCacheEntriesInternalAsync<T, TFormat>(CacheKey[] keys, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();
        if (keys.Length == 0)
        {
            return [];
        }
        var redisKeys = Array.ConvertAll(keys, k => ToRedisKey(k, token));
        if (!IsConnected)
        {
            return GetDefaultEntries<T>(keys);
        }

        var operation = StartOperation<T>(nameof(GetCacheEntriesInternalAsync));
        var retValues = GetDefaultEntries<T>(keys);
        var reads = InitReads(redisKeys);
        List<(RedisKey Key, RedisValue Read, int FormatId)>? unreadableKeys = null;
        try
        {
            await EnsureConnectedAsync(_read, token).ConfigureAwait(false);
            var groups = SlotGroups(redisKeys, _logger);
            var groupReads = await PerSlotAsync(
                groups,
                (Self: this, Keys: redisKeys, Token: token),
                static async (s, group) =>
                {
                    var read = new StrongBox<StrongBox<(RedisValue[] Values, DateTimeOffset?[] Expirations)>?>();
                    var committed = await s.Self._read.ExecuteAsync(
                    ReadManyAsync,
                    (s.Self, Keys: Pick(s.Keys, group), Read: read),
                    default,
                    s.Token).ConfigureAwait(false);
                    return committed ? read.Value : null;
                },
                null,
                LogRedisCacheException).ConfigureAwait(false);

            for (var g = 0; g < groups.Length; g++)
            {
                // A slot whose read did not commit reads as misses; the others keep their entries.
                if (groupReads[g] is not { } result)
                {
                    continue;
                }

                var group = groups[g];
                var (values, expirations) = result.Value;
                for (var j = 0; j < group.Length; j++)
                {
                    var i = group[j];
                    var (found, deserialized) = InterpretBatchRead<T, TFormat>(redisKeys[i], values[j], format, ref unreadableKeys);
                    if (!found)
                    {
                        continue;
                    }
                    reads[i].Hit = true;
                    retValues[i] = new KeyValuePair<CacheKey, ICacheEntry<T?>>(
                        keys[i],
                        _cacheEntryFactory.Create<T?>(deserialized, Clock.ToDateTimeOffset(expirations[j])));
                }
            }
            operation.Stop();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            operation.Stop();
            LogRedisCacheException(ex);
            retValues = GetDefaultEntries<T>(keys);
            reads = InitReads(redisKeys);
        }
        finally
        {
            TrackPerKey(operation, reads);
        }

        if (unreadableKeys is not null)
        {
            await RemoveUnreadableAsync<T, TFormat>(unreadableKeys, format, token).ConfigureAwait(false);
        }

        return retValues;
    }

    private ICacheEntry<T?> DefaultEntry<T>() =>
        _cacheEntryFactory.Create<T?>(default, DateTimeOffset.MinValue);

    private KeyValuePair<CacheKey, ICacheEntry<T?>>[] GetDefaultEntries<T>(CacheKey[] keys) =>
        [.. keys.Select(k => new KeyValuePair<CacheKey, ICacheEntry<T?>>(k, DefaultEntry<T>()))];
}
