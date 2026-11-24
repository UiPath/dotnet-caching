namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    private async ValueTask<bool> SetMetadataCoreAsync<T>(CacheKey cacheKey, IDictionary<string, string?> metadata, ICacheValueSerializer? writer, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        NoteWritten(redisKey);
        var ret = false;
        var operation = StartOperation<T>(nameof(SetMetadataAsync));
        try
        {
            var keyExists = await _read.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return s.Self.Database.KeyExistsAsync(s.Key, s.Self.ReadFlagsFor(s.Key)).AsValueTask();
            },
            (Self: this, Key: redisKey),
            default,
            token).ConfigureAwait(false);
            if (keyExists)
            {
                if (metadata.Count > 0)
                {
                    var (metadataValue, payload) = SerializeFieldValue(metadata, writer);
                    if (_auditLargeValues)
                    {
                        AuditKeySize(Logged(cacheKey, redisKey, typeof(T)), KnownFieldNames.MetadataKey, metadataValue);
                    }

                    using (payload)
                    {
                        if (_cacheNullValues)
                        {
                            ret = await _write.ExecuteAsync(static async (s, token) =>
                            {
                                token.ThrowIfCancellationRequested();
                                var transaction = s.Self.Database.CreateTransaction();
                                transaction.AddCondition(Condition.KeyExists(s.Key));
                                transaction.HashSetAsync(s.Key, KnownFieldNames.MetadataKey, s.MetadataValue, When.Always, CommandFlags.DemandMaster).Forget();
                                return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
                            },
                            (Self: this, Key: redisKey, MetadataValue: metadataValue),
                            default,
                            token).ConfigureAwait(false);
                        }
                        else
                        {
                            ret = await _write.ExecuteAsync(static async (s, token) =>
                            {
                                token.ThrowIfCancellationRequested();
                                await s.Self.Database.HashSetAsync(s.Key, KnownFieldNames.MetadataKey, s.MetadataValue, When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
                                return true;
                            },
                            (Self: this, Key: redisKey, MetadataValue: metadataValue),
                            default,
                            token).ConfigureAwait(false);
                        }
                    }
                }
                else if (_cacheNullValues)
                {
                    ret = await _write.ExecuteAsync(static async (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        var transaction = s.Self.Database.CreateTransaction();
                        transaction.AddCondition(Condition.KeyExists(s.Key));
                        transaction.HashSetAsync(s.Key, KnownFieldNames.MetadataKey, RedisValue.EmptyString, When.Always, CommandFlags.DemandMaster).Forget();
                        return await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
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
                       return s.Self.Database.HashDeleteAsync(s.Key, KnownFieldNames.MetadataKey, CommandFlags.DemandMaster).AsValueTask();
                   },
                   (Self: this, Key: redisKey),
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
            LogRedisHashCacheException(ex);
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(ret);
        }

        return ret;
    }

    /// <summary>Deletes the metadata field an earlier write left, after a chunked write: a replacement too large for one command is not atomic anyway.</summary>
    private async ValueTask<bool> DropMetadataAfterAsync(RedisKey redisKey, ValueTask<bool> write, CancellationToken token)
    {
        if (!await write.ConfigureAwait(false))
        {
            return false;
        }

        var dropped = false;
        try
        {
            dropped = await _write.ExecuteAsync(static async (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                await s.Self.Database.HashDeleteAsync(s.Key, KnownFieldNames.MetadataKey, CommandFlags.DemandMaster).ConfigureAwait(false);
                return true;
            },
            (Self: this, Key: redisKey),
            default,
            token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogRedisHashCacheException(ex);
        }
        finally
        {
            NoteWritten(redisKey);
        }

        return dropped;
    }

    /// <summary>Returns the metadata's payload, which the caller disposes once the transaction has executed.</summary>
    private SerializedPayload QueueMetadataWrite(ITransaction transaction, LoggedKey logged, RedisKey redisKey, IDictionary<string, string?>? metadata, ICacheValueSerializer? writer, List<Task>? commands = null)
    {
        metadata = MetadataToWrite(metadata, writer);
        if (metadata != null)
        {
            if (_cacheNullValues)
            {
                transaction.AddCondition(Condition.KeyExists(redisKey));
            }
            var (value, payload) = SerializeFieldValue(metadata, writer);
            if (_auditLargeValues)
            {
                AuditKeySize(logged, KnownFieldNames.MetadataKey, value);
            }

            try
            {
                var hashEntries = new[] { new HashEntry(KnownFieldNames.MetadataKey, value) };
                var set = transaction.HashSetAsync(redisKey, hashEntries, CommandFlags.DemandMaster);
                set.Forget();
                commands?.Add(set);
            }
            catch
            {
                payload.Dispose();
                throw;
            }
            return payload;
        }
        if (_cacheNullValues)
        {
            transaction.AddCondition(Condition.KeyExists(redisKey));
            var marker = transaction.HashSetAsync(redisKey, EmptyMarkerEntries(), CommandFlags.DemandMaster);
            marker.Forget();
            commands?.Add(marker);
            return default;
        }
        var drop = transaction.HashDeleteAsync(redisKey, new RedisValue(KnownFieldNames.MetadataKey), CommandFlags.DemandMaster);
        drop.Forget();
        commands?.Add(drop);
        return default;
    }

    /// <summary>The metadata a write asks to store: none for an empty hash unless nulls are cached, which keeps the empty hash as its metadata.</summary>
    private IDictionary<string, string?>? MetadataRequested(int valueCount, IDictionary<string, string?>? metadata) =>
        valueCount > 0 || _cacheNullValues ? metadata : null;

    /// <summary>With a non-JSON serializer the metadata field is left out when it is empty and nulls are not cached, so the hash reads back with none; the JSON default keeps writing it as it always has.</summary>
    private IDictionary<string, string?>? MetadataToWrite(IDictionary<string, string?>? metadata, ICacheValueSerializer? writer) =>
        writer is not null && !_cacheNullValues && metadata is { Count: 0 } ? null : metadata;
}
