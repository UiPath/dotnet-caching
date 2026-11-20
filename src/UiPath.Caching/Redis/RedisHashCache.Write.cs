namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    private static void Release(SerializedPayload[] payloads)
    {
        foreach (var payload in payloads)
        {
            payload.Dispose();
        }
    }

    private ValueTask<bool> SetCountingAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CachePolicy? policy, WriteReport? report, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        HashFields.ValidateForWrite(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var writer = FormatFor(policy, forWrite: true).Writer;
        var requested = values.Count > 0 || _cacheNullValues ? options.Metadata : null;
        var metadata = MetadataToWrite(requested, writer);
        var expiration = GetExpiration(options, policy);
        var setOption = values.Count == 0 && _cacheNullValues ? HashCacheSetOption.KeyReplace : options.SetOption;

        // Everything that can throw runs before the fields and the metadata rent buffers, which SetInnerAsync then owns.
        var (entries, payloads) = SerializeFields(values, metadata, writer);
        if (requested is not null && metadata is null && setOption == HashCacheSetOption.HashReplace)
        {
            // A replacement too large for one transaction drops the stale metadata field after its chunks, since it is not atomic anyway.
            return entries.Length > _fieldBatchSize
                ? DropMetadataAfterAsync(redisKey, SetInnerAsync<T>(cacheKey, redisKey, entries, payloads, setOption, expiration, token, report), token)
                : SetInnerAsync<T>(cacheKey, redisKey, entries, payloads, setOption, expiration, token, report, dropMetadata: true);
        }

        return SetInnerAsync<T>(cacheKey, redisKey, entries, payloads, setOption, expiration, token, report);
    }

    private ValueTask<bool> SetCoreAsync<T>(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset effective, CachePolicy? policy, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        HashFields.ValidateForWrite(values);
        var redisKey = ToRedisKey(cacheKey, token);
        var (hashEntries, payloads) = SerializeFields(values, metadata: null, FormatFor(policy, forWrite: true).Writer);
        return SetInnerAsync<T>(cacheKey, redisKey, hashEntries, payloads, HashCacheSetOption.KeyReplace, effective, token);
    }

    /// <summary>One entry per field, then the metadata entry when there is metadata; the payloads go back once the write has completed.</summary>
    private (HashEntry[] Entries, SerializedPayload[] Payloads) SerializeFields<T>(IDictionary<string, T?> values, IDictionary<string, string?>? metadata, ICacheValueSerializer? writer)
    {
        var count = metadata is null ? values.Count : values.Count + 1;
        var entries = new HashEntry[count];
        var payloads = new SerializedPayload[count];
        var i = 0;
        try
        {
            var format = SerializerMetricsOn && !_valueSerializers.IsEmpty ? _valueSerializers.NameOf(writer) : null;
            foreach (var kv in values)
            {
                (var value, payloads[i]) = SerializeFieldValue(kv.Value, writer);
                entries[i++] = new HashEntry(kv.Key, value);
                if (format is not null)
                {
                    TrackSerializer(Metrics.SerializerWrite, format, MetricCache);
                }
            }
            if (metadata is not null)
            {
                (var value, payloads[i]) = SerializeFieldValue(metadata, writer);
                entries[i] = new HashEntry(KnownFieldNames.MetadataKey, value);
            }
        }
        catch
        {
            Release(payloads);
            throw;
        }
        return (entries, payloads);
    }

    /// <summary>Borrowed memory is safe here because every write awaits its command and the connection copies the value while writing it; pooled memory goes back only then.</summary>
    private (RedisValue Value, SerializedPayload Payload) SerializeFieldValue<T>(T? value, ICacheValueSerializer? writer)
    {
        if (_cacheNullValues && IsDefault(value))
        {
            return (RedisValue.EmptyString, default);
        }
        if (writer is not null)
        {
            var headered = SerializedPayload.Serialize(writer, _valueSerializers.FormatIdsOf(writer).Span, value!);
            return (headered.Memory, headered);
        }
        if (_memorySerializer is { } memory)
        {
            var payload = SerializedPayload.Serialize(memory, value);
            return (payload.Memory, payload);
        }
        return (_serializer.Serialize(value), default);
    }

    /// <summary>Owns <paramref name="payloads"/>: they go back to the pool on every exit, including one before the command starts.</summary>
    private async ValueTask<bool> SetInnerAsync<T>(CacheKey cacheKey, RedisKey redisKey, HashEntry[] hashEntries, SerializedPayload[] payloads, HashCacheSetOption setOption, DateTimeOffset expiration, CancellationToken token, WriteReport? report = null, bool dropMetadata = false)
    {
        try
        {
            var now = Clock.GetUtcNow();
            var ret = false;
            if (token.IsCancellationRequested || !IsConnected)
            {
                token.ThrowIfCancellationRequested();
                return ret;
            }

            if (_auditLargeValues && expiration >= now)
            {
                AuditKeySize(Logged(cacheKey, redisKey, typeof(T)), hashEntries);
            }

            if (hashEntries.Length == 0 && expiration >= now && _cacheNullValues)
            {
                hashEntries = [new HashEntry(KnownFieldNames.MetadataKey, RedisValue.EmptyString)];
                setOption = HashCacheSetOption.KeyReplace;
            }

            // A chunked write is counted after its chunks land, not with them, so peers never get its fields.
            if (hashEntries.Length <= _fieldBatchSize)
            {
                report?.Capture(hashEntries, setOption);
            }

            NoteWritten(redisKey);
            var operation = StartOperation<T>(nameof(SetAsync));
            try
            {
                ret = await WriteAsync(redisKey, hashEntries, setOption, expiration, now, report, dropMetadata, token).ConfigureAwait(false);
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
                LogRedisHashCacheException(ex);
            }
            finally
            {
                NoteWritten(redisKey);
                operation.Track(ret);
            }

            return ret;
        }
        finally
        {
            Release(payloads);
        }
    }

    /// <summary>A replacement needs a staging key on the key's slot; a merge always can.</summary>
    private bool TryChunkedWriteKeys(RedisKey redisKey, HashCacheSetOption setOption, out RedisKey staging)
    {
        staging = default;
        if (setOption == HashCacheSetOption.HashReplace)
        {
            return true;
        }

        var text = redisKey.ToString();
        var suffix = ":stage:" + Guid.NewGuid().ToString("N");
        if (RedisHashTag.HasValidTag(text))
        {
            staging = text + suffix;
        }
        else if (text.Length > 0 && RedisHashTag.ContainsNoBraces(text))
        {
            staging = "{" + text + "}" + suffix;
        }
        else
        {
            return false;
        }

        return SameSlot(redisKey, staging, _logger);
    }

    private async ValueTask<bool> WriteAsync(RedisKey redisKey, HashEntry[] hashEntries, HashCacheSetOption setOption, DateTimeOffset expiration, DateTimeOffset now, WriteReport? report, bool dropMetadata, CancellationToken token)
    {
        if (expiration < now || hashEntries.Length == 0)
        {
            return await DeleteKeyAsync(redisKey, token).ConfigureAwait(false);
        }

        if (hashEntries.Length > _fieldBatchSize && TryChunkedWriteKeys(redisKey, setOption, out var staging))
        {
            await EnsureConnectedAsync(_write, token).ConfigureAwait(false);
            return await _write.ExecuteAsync(static (s, token) => s.Self.WriteInChunksAsync(s.Key, s.Staging, s.Entries, s.Option, s.Expiration, s.Report, token), (Self: this, Key: redisKey, Staging: staging, Entries: hashEntries, Option: setOption, Expiration: expiration, Report: report), default, token).ConfigureAwait(false);
        }

        return await WriteInTransactionAsync(redisKey, hashEntries, setOption, expiration, report, dropMetadata, token).ConfigureAwait(false);
    }

    private ValueTask<bool> DeleteKeyAsync(RedisKey redisKey, CancellationToken token) =>
        _write.ExecuteAsync(
            static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster).AsValueTask();
            },
            (Self: this, Key: redisKey),
            default,
            token);

    private async ValueTask<bool> WriteInTransactionAsync(RedisKey redisKey, HashEntry[] hashEntries, HashCacheSetOption setOption, DateTimeOffset expiration, WriteReport? report, bool dropMetadata, CancellationToken token)
    {
        await EnsureConnectedAsync(_write, token).ConfigureAwait(false);
        var transaction = Database.CreateTransaction();
        if (setOption == HashCacheSetOption.KeyReplace)
        {
            transaction.KeyDeleteAsync(redisKey).Forget();
        }

        transaction.HashSetAsync(redisKey, hashEntries, CommandFlags.DemandMaster).Forget();
        if (dropMetadata && setOption == HashCacheSetOption.HashReplace)
        {
            // In the write's own transaction, where a concurrent writer's newer metadata cannot be hit.
            transaction.HashDeleteAsync(redisKey, KnownFieldNames.MetadataKey, CommandFlags.DemandMaster).Forget();
        }

        Task<long>? counted = null;
        if (report is not null)
        {
            transaction.HashSetAsync(redisKey, KnownFieldNames.VersionKey, HashVersion.NewEpochSeed(), When.NotExists, CommandFlags.DemandMaster).Forget();
            counted = transaction.HashIncrementAsync(redisKey, KnownFieldNames.VersionKey, 1, CommandFlags.DemandMaster);
            counted.Forget();
        }

        if (expiration != DateTimeOffset.MaxValue)
        {
            await transaction.KeyExpireAsync(redisKey, expiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).ConfigureAwait(false);
        }
        else if (setOption == HashCacheSetOption.HashReplace)
        {
            // The key survives a HashReplace, and so would a TTL an earlier write gave it.
            transaction.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget).Forget();
        }

        var committed = await _write.ExecuteAsync(
            static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.ExecuteAsync(CommandFlags.DemandMaster).AsValueTask();
            },
            transaction,
            default,
            token).ConfigureAwait(false);
        if (!committed)
        {
            LogRedisTransactionFailed();
        }
        else if (counted is not null)
        {
            report!.Version = await VersionOrUncountedAsync(counted).ConfigureAwait(false);
        }

        return committed;
    }

    /// <summary>Zero when the key is gone: counting would make a key of the count alone, with no lifetime. Zero too when the count cannot be made: the chunks it follows have landed.</summary>
    private async Task<long> CountWriteAsync(RedisKey redisKey)
    {
        try
        {
            var transaction = Database.CreateTransaction();
            transaction.AddCondition(Condition.KeyExists(redisKey));
            transaction.HashSetAsync(redisKey, KnownFieldNames.VersionKey, HashVersion.NewEpochSeed(), When.NotExists, CommandFlags.DemandMaster).Forget();
            var counted = transaction.HashIncrementAsync(redisKey, KnownFieldNames.VersionKey, 1, CommandFlags.DemandMaster);
            counted.Forget();
            return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false) ? await VersionOrUncountedAsync(counted).ConfigureAwait(false) : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogVersionNotCounted(ex);
            return 0;
        }
    }

    /// <summary>Zero, an uncounted write that peers drop on, when the count refuses to move: it stops at the 64-bit maximum while the write beside it still lands.</summary>
    private async Task<long> VersionOrUncountedAsync(Task<long> counted)
    {
        try
        {
            return await counted.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogVersionNotCounted(ex);
            return 0;
        }
    }
}
