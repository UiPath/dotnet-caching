using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache : IFillManyTier
{
    public ValueTask<FillResult<T>[]> TryFillManyAsync<T>(KeyValuePair<CacheKey, T?>[] entries, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token, TimeSpan? generatorElapsed = null)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var format = FormatFor(policy, forWrite: true, alsoReads: true);
        var deadline = GetExpiration(expiration);
        var fence = generatorElapsed is { } elapsed ? NewFence(elapsed) : default;
        return format.IsDefault
            ? TryFillManyInternalAsync(entries, deadline, default(PlainFormat), fence, token)
            : TryFillManyInternalAsync(entries, deadline, format, fence, token);
    }

    private static List<int> StorableSlotPositions(bool[] storable, int[] group)
    {
        var open = new List<int>(group.Length);
        for (var j = 0; j < group.Length; j++)
        {
            if (storable[group[j]])
            {
                open.Add(j);
            }
        }

        return open;
    }

    private static async ValueTask<FillRaw?[]?> FillSlotOnceAsync(RedisCache self, RedisKey[] keys, RedisValue[] serialized, bool[] storable, int[] group, DateTimeOffset deadline, FillFence fence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var raws = new FillRaw?[group.Length];
        var open = StorableSlotPositions(storable, group);

        if (self.RewriteScriptsOn)
        {
            var scripted = open.Select(j => FillAfterAsync(self, keys[group[j]], serialized[group[j]], deadline, lost: false, fence, token).AsTask()).ToArray();
            for (var k = 0; k < open.Count; k++)
            {
                raws[open[k]] = (await self.SettleAsync(scripted[k]).ConfigureAwait(false)).Value;
            }

            return raws;
        }

        var remaining = self.RemainingOrZero(deadline);
        if (remaining <= TimeSpan.Zero)
        {
            foreach (var j in open)
            {
                raws[j] = FillRaw.Lapsed;
            }

            return raws;
        }

        // One flush for the slot: every key is sent before any reply is awaited.
        var batch = self.Database.CreateBatch();
        var sets = new Task<bool>[open.Count];
        for (var k = 0; k < open.Count; k++)
        {
            var i = group[open[k]];
            sets[k] = batch.StringSetAsync(keys[i], serialized[i], remaining, When.NotExists, CommandFlags.DemandMaster);
        }

        batch.Execute();

        // Each command answers for its own key: one that failed leaves the others' outcomes standing.
        List<int>? lost = null;
        for (var k = 0; k < open.Count; k++)
        {
            switch (await self.SettleAsync(sets[k]).ConfigureAwait(false))
            {
                case (true, true):
                    raws[open[k]] = new FillRaw(true, RedisValue.Null, DateTimeOffset.MaxValue);
                    break;
                case (true, false):
                    (lost ??= []).Add(open[k]);
                    break;
            }
        }

        if (lost is not null)
        {
            var reads = lost.Select(j => FillAfterAsync(self, keys[group[j]], serialized[group[j]], deadline, lost: true, fence, token).AsTask()).ToArray();
            for (var k = 0; k < lost.Count; k++)
            {
                raws[lost[k]] = (await self.SettleAsync(reads[k]).ConfigureAwait(false)).Value;
            }
        }

        return raws;
    }

    /// <summary>The command's answer, or none when it failed; a cancellation is not a failure of the key.</summary>
    private async ValueTask<(bool Answered, TResult? Value)> SettleAsync<TResult>(Task<TResult> command)
    {
        try
        {
            return (true, await command.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRedisCacheException(ex);
            return (false, default);
        }
    }

    private async ValueTask<FillResult<T>[]> TryFillManyInternalAsync<T, TFormat>(KeyValuePair<CacheKey, T?>[] entries, DateTimeOffset deadline, TFormat format, FillFence fence, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        token.ThrowIfCancellationRequested();
        var results = new FillResult<T>[entries.Length];
        var redisKeys = Array.ConvertAll(entries, e => ToRedisKey(e.Key, token));
        if (!IsConnected || entries.Length == 0)
        {
            return results;
        }

        var operation = StartOperation<T>(nameof(TryFillAsync));
        var serialized = new RedisValue[entries.Length];
        var payloads = new SerializedPayload[entries.Length];
        var storable = new bool[entries.Length];
        var ok = false;
        NoteWritten(redisKeys);
        try
        {
            for (var i = 0; i < entries.Length; i++)
            {
                var isNull = IsDefault(entries[i].Value);
                if (!CanStore(entries[i].Key, redisKeys[i], isNull, typeof(T)))
                {
                    continue;
                }

                storable[i] = true;
                (serialized[i], payloads[i]) = isNull ? (RedisValue.EmptyString, default(SerializedPayload)) : SerializeValue(entries[i].Value, format);
                _auditKeySize?.Invoke(Logged(entries[i].Key, redisKeys[i], typeof(T)), serialized[i]);
            }

            await EnsureConnectedAsync(_write, token).ConfigureAwait(false);
            var groups = SlotGroups(redisKeys, _logger);
            var slots = await PerSlotAsync(
                groups,
                (Self: this, Keys: redisKeys, Serialized: serialized, Storable: storable, Deadline: deadline, Fence: fence, Token: token),
                static (s, group) => s.Self._write.ExecuteAsync(
                    static (x, token) => FillSlotOnceAsync(x.Self, x.Keys, x.Serialized, x.Storable, x.Group, x.Deadline, x.Fence, token),
                    (s.Self, s.Keys, s.Serialized, s.Storable, Group: group, s.Deadline, s.Fence),
                    null,
                    s.Token),
                null,
                LogRedisCacheException).ConfigureAwait(false);

            ReadFillSlotResults(entries, redisKeys, storable, groups, slots, deadline, format, results, ref ok);

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
            Array.Clear(results);
            ok = false;
        }
        finally
        {
            foreach (var payload in payloads)
            {
                payload.Dispose();
            }

            NoteWritten(redisKeys);
            operation.Track(ok, entries.Length);
        }

        return results;
    }

    private void ReadFillSlotResults<T, TFormat>(
        KeyValuePair<CacheKey, T?>[] entries,
        RedisKey[] redisKeys,
        bool[] storable,
        int[][] groups,
        FillRaw?[]?[] slots,
        DateTimeOffset deadline,
        TFormat format,
        FillResult<T>[] results,
        ref bool ok)
        where TFormat : struct, IValueFormat
    {
        ok = true;
        for (var g = 0; g < groups.Length; g++)
        {
            for (var j = 0; j < groups[g].Length; j++)
            {
                var i = groups[g][j];
                if (!storable[i])
                {
                    continue;
                }

                results[i] = slots[g]?[j] switch
                {
                    { IsLapsed: true } => LapsedFill<T>(entries[i].Key, redisKeys[i]),
                    { IsRemoved: true } => RemovedFill<T>(entries[i].Key, redisKeys[i]),
                    { Added: true } => new FillResult<T>(FillOutcome.Added, default, deadline),
                    { } stored => ExistingEntry<T, TFormat>(redisKeys[i], stored, format),
                    _ => default,
                };
                ok &= results[i].Outcome is not (FillOutcome.Failed or FillOutcome.Unreadable);
            }
        }
    }

    private FillResult<T> RemovedFill<T>(CacheKey cacheKey, RedisKey redisKey)
    {
        LogFillSkipped(Logged(cacheKey, redisKey, typeof(T)), "the key was removed while its generator ran");
        return new FillResult<T>(FillOutcome.Removed, default);
    }

    private FillResult<T> LapsedFill<T>(CacheKey cacheKey, RedisKey redisKey)
    {
        LogFillSkipped(Logged(cacheKey, redisKey, typeof(T)), ExpirationPassed);
        return default;
    }
}
