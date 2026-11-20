namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    private ValueTask<bool> RemoveFieldsCoreAsync<T>(CacheKey cacheKey, string[] fields, bool versioned, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        HashFields.ValidateFieldsForWrite(fields);
        var redisKey = ToRedisKey(cacheKey, token);
        if (fields.Length == 0)
        {
            return new ValueTask<bool>(true);
        }

        return RunAsync(_write, StartOperation<T>(), static (s, token) => s.Self.DeleteFieldsAsync(s.Key, s.Fields, s.Versioned, token), (Self: this, Key: redisKey, Fields: fields, Versioned: versioned), false, static removed => removed, _logFailure, token);
    }

    private async Task<long> DeleteFieldsCountingAsync(RedisKey redisKey, RedisValue[] chunk)
    {
        var transaction = Database.CreateTransaction();
        transaction.AddCondition(Condition.KeyExists(redisKey));
        transaction.HashSetAsync(redisKey, KnownFieldNames.VersionKey, HashVersion.NewEpochSeed(), When.NotExists, CommandFlags.DemandMaster).Forget();
        transaction.HashIncrementAsync(redisKey, KnownFieldNames.VersionKey, 1, CommandFlags.DemandMaster).Forget();
        transaction.HashDeleteAsync(redisKey, chunk, CommandFlags.DemandMaster).Forget();
        await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
        return 0;
    }

    private ValueTask<bool> DeleteFieldsAsync(RedisKey redisKey, string[] fields, bool versioned, CancellationToken token)
    {
        NoteWritten(redisKey);
        return NoteWrittenAfterAsync(redisKey, DeleteFieldsCoreAsync(redisKey, fields, versioned, token));
    }

    private async ValueTask<bool> DeleteFieldsCoreAsync(RedisKey redisKey, string[] fields, bool versioned, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pending = new List<Task<long>>((fields.Length + _fieldBatchSize - 1) / _fieldBatchSize);
        for (var start = 0; start < fields.Length; start += _fieldBatchSize)
        {
            var chunk = new RedisValue[Math.Min(_fieldBatchSize, fields.Length - start)];
            for (var i = 0; i < chunk.Length; i++)
            {
                chunk[i] = fields[start + i];
            }

            pending.Add(versioned && start == 0 ? DeleteFieldsCountingAsync(redisKey, chunk) : Database.HashDeleteAsync(redisKey, chunk, CommandFlags.DemandMaster));
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        if (versioned)
        {
            await RemoveCountOfEmptiedHashAsync(redisKey).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Redis deletes a hash whose last field goes; the count alone would keep the key, with its lifetime, as a hash no read can find a value in.</summary>
    private async Task RemoveCountOfEmptiedHashAsync(RedisKey redisKey)
    {
        var transaction = Database.CreateTransaction();
        transaction.AddCondition(Condition.HashLengthEqual(redisKey, 1));
        transaction.AddCondition(Condition.HashExists(redisKey, KnownFieldNames.VersionKey));
        transaction.HashDeleteAsync(redisKey, KnownFieldNames.VersionKey, CommandFlags.DemandMaster).Forget();
        await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
    }
}
