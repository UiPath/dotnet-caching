namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    /// <summary>Throws a transaction's own failure, not the cancellation of what it queued.</summary>
    private static async Task CommitAsync(ITransaction transaction, string what, RedisKey key, Task first, Task second)
    {
        bool committed;
        try
        {
            committed = await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch
        {
            first.Forget();
            second.Forget();
            throw;
        }

        if (!committed)
        {
            first.Forget();
            second.Forget();
            throw new RedisException($"{what} of {key} did not commit.");
        }

        await ObserveAllAsync([first, second]).ConfigureAwait(false);
    }

    private static async Task SettleAsync(List<Task> pending)
    {
        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Observed; the caller rethrows its own.
            }
        }
    }

    /// <summary>Awaits every task and rethrows the first failure.</summary>
    private static async Task ObserveAllAsync(List<Task> pending)
    {
        Exception? first = null;
        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                first ??= ex;
            }
        }

        if (first is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
        }
    }

    /// <summary>A merge is not atomic across chunks; a replacement fills a staging key and renames it over the key.</summary>
    private async ValueTask<bool> WriteInChunksAsync(RedisKey redisKey, RedisKey staging, HashEntry[] entries, HashCacheSetOption setOption, DateTimeOffset expiration, WriteReport? report, CancellationToken token, SendTrace? trace = null)
    {
        token.ThrowIfCancellationRequested();
        trace?.MarkSent();
        var target = setOption == HashCacheSetOption.HashReplace ? redisKey : staging;
        var pending = new List<Task>((entries.Length / _fieldBatchSize) + 1);
        try
        {
            if (!await WriteChunkSetAsync(target, entries, setOption, expiration, report, trace, pending).ConfigureAwait(false))
            {
                return false;
            }

            await ObserveAllAsync(pending).ConfigureAwait(false);
            if (report is not null)
            {
                // Apart from the chunks, which are not atomic as a whole; the write reads as unversioned if the key is already gone.
                report.Version = await CountWriteAsync(target).ConfigureAwait(false);
            }

            if (setOption == HashCacheSetOption.KeyReplace)
            {
                return await PublishStagedAsync(redisKey, staging, expiration, report, trace).ConfigureAwait(false);
            }

            trace?.MarkAnswered(true);
            return true;
        }
        catch
        {
            // A chunk still in flight reads the caller's buffers, which are released once this returns.
            await SettleAsync(pending).ConfigureAwait(false);
            DropReplacementStaging(staging, setOption);
            throw;
        }
    }

    /// <summary>Renames the staging key over the key; true when the replacement is the key's value now.</summary>
    private async ValueTask<bool> PublishStagedAsync(RedisKey redisKey, RedisKey staging, DateTimeOffset expiration, WriteReport? report, SendTrace? trace)
    {
        if (OnStaged is { } staged)
        {
            await staged(Database, staging).ConfigureAwait(false);
        }

        try
        {
            if (report?.OnlyIfAbsent == true)
            {
                if (!await RenameIfAbsentAsync(staging, redisKey, expiration).ConfigureAwait(false))
                {
                    report.Existed = true;
                    DropStaging(staging);
                    trace?.MarkAnswered(false);
                    return false;
                }
            }
            else
            {
                await RenameOverAsync(staging, redisKey, expiration).ConfigureAwait(false);
            }
        }
        catch (Exception renameFailed) when (expiration < Clock.GetUtcNow())
        {
            // A fill of an absent key must not delete what is held, so its expired replacement is only dropped.
            var deleted = report?.OnlyIfAbsent != true && await DeleteExpiredReplacementAsync(redisKey, renameFailed).ConfigureAwait(false);

            // A cleanup that finds nothing to delete proves nothing: the rename may have applied and its replacement expired since, so the write stays unanswered, and unknown, unless the cleanup itself removed the key.
            if (deleted)
            {
                trace?.MarkAnswered(true);
            }

            return deleted;
        }

        trace?.MarkAnswered(true);
        return true;
    }

    private async ValueTask<bool> WriteChunkSetAsync(RedisKey target, HashEntry[] entries, HashCacheSetOption setOption, DateTimeOffset expiration, WriteReport? report, SendTrace? trace, List<Task> pending)
    {
        if (trace is null || setOption != HashCacheSetOption.HashReplace || report?.OnlyIfAbsent == true)
        {
            QueueChunks(target, entries, setOption, expiration, pending);
            return true;
        }

        // A traced merge acts on an absent key or a hash only, which each chunk checks in the step that writes it; one after the other, so that a refusal says whether an earlier chunk landed.
        var index = 0;
        for (var start = 0; start < entries.Length; start += _fieldBatchSize, index++)
        {
            if (OnChunk is { } hook)
            {
                await hook(index).ConfigureAwait(false);
            }

            try
            {
                if (expiration <= Clock.GetUtcNow())
                {
                    if (index == 0)
                    {
                        trace.MarkAnswered(false);
                    }

                    return false;
                }

                await WriteGuardedChunkAsync(target, entries.AsSpan(start, Math.Min(_fieldBatchSize, entries.Length - start)).ToArray(), expiration).ConfigureAwait(false);
            }
            catch (HashTypeRefusedException) when (index == 0)
            {
                // The first chunk was refused, so nothing was applied.
                trace.MarkAnswered(false);
                return false;
            }
        }

        return true;
    }

    private void QueueChunks(in RedisKey target, HashEntry[] entries, HashCacheSetOption setOption, DateTimeOffset expiration, List<Task> pending)
    {
        // Any chunk can create the key after an earlier chunk failed or its deadline passed.
        for (var start = 0; start < entries.Length; start += _fieldBatchSize)
        {
            pending.Add(WriteWithLifetimeAsync(target, entries.AsSpan(start, Math.Min(_fieldBatchSize, entries.Length - start)).ToArray(), setOption, expiration));
        }
    }

    private void DropReplacementStaging(in RedisKey staging, HashCacheSetOption setOption)
    {
        if (setOption == HashCacheSetOption.KeyReplace)
        {
            DropStaging(staging);
        }
    }

    // The replacement expired before it took the key's place, its staging key with it: as an expired write does, it leaves the key gone.
    private async Task<bool> DeleteExpiredReplacementAsync(RedisKey redisKey, Exception renameFailed)
    {
        // A staging key that expired with its deadline is the expected cause; a timeout or a lost connection is not.
        if (renameFailed is RedisServerException && renameFailed.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
        {
            LogExpiredReplacement(renameFailed);
        }
        else
        {
            LogExpiredReplacementRenameFailed(renameFailed);
        }

        try
        {
            return await Database.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRedisHashCacheException(ex);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(renameFailed);
            throw;
        }
    }

    // The cleanup must not replace the failure that caused it.
    private void DropStaging(RedisKey staging)
    {
        try
        {
            Database.KeyDeleteAsync(staging, CommandFlags.DemandMaster | CommandFlags.FireAndForget).Forget();
        }
        catch (Exception ex)
        {
            LogRedisHashCacheException(ex);
        }
    }

    /// <summary>A hash with no expiry drops the staging key's orphan lifetime in the same transaction.</summary>
    private async Task RenameOverAsync(RedisKey staging, RedisKey redisKey, DateTimeOffset expiration)
    {
        if (expiration != DateTimeOffset.MaxValue)
        {
            await Database.KeyRenameAsync(staging, redisKey, When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
            return;
        }

        // Redis runs the rest of a transaction past a failed command: without the staging key, PERSIST would make the old hash immortal.
        var transaction = Database.CreateTransaction();
        transaction.AddCondition(Condition.KeyExists(staging));
        var renamed = transaction.KeyRenameAsync(staging, redisKey, When.Always, CommandFlags.DemandMaster);
        var persisted = transaction.KeyPersistAsync(redisKey, CommandFlags.DemandMaster);
        await CommitAsync(transaction, "The replacement", redisKey, renamed, persisted).ConfigureAwait(false);
    }

    /// <summary>Renames the staging key to the key only if the key is absent; false when it is held, or the staging key is gone.</summary>
    private async Task<bool> RenameIfAbsentAsync(RedisKey staging, RedisKey redisKey, DateTimeOffset expiration)
    {
        if (expiration != DateTimeOffset.MaxValue)
        {
            return await Database.KeyRenameAsync(staging, redisKey, When.NotExists, CommandFlags.DemandMaster).ConfigureAwait(false);
        }

        // As the replacement drops the staging key's orphan lifetime, but only if the rename took the key's place.
        var transaction = Database.CreateTransaction();
        transaction.AddCondition(Condition.KeyExists(staging));
        transaction.AddCondition(Condition.KeyNotExists(redisKey));
        var renamed = transaction.KeyRenameAsync(staging, redisKey, When.Always, CommandFlags.DemandMaster);
        var persisted = transaction.KeyPersistAsync(redisKey, CommandFlags.DemandMaster);
        if (!await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false))
        {
            renamed.Forget();
            persisted.Forget();
            return false;
        }

        await ObserveAllAsync([renamed, persisted]).ConfigureAwait(false);
        return true;
    }

    private async Task WriteWithLifetimeAsync(RedisKey target, HashEntry[] chunk, HashCacheSetOption setOption, DateTimeOffset expiration)
    {
        var transaction = Database.CreateTransaction();
        var written = transaction.HashSetAsync(target, chunk, CommandFlags.DemandMaster);
        Task lifetime;
        if (expiration != DateTimeOffset.MaxValue)
        {
            lifetime = transaction.KeyExpireAsync(target, expiration.UtcDateTime, CommandFlags.DemandMaster);
        }
        else if (setOption == HashCacheSetOption.HashReplace)
        {
            lifetime = transaction.KeyPersistAsync(target, CommandFlags.DemandMaster);
        }
        else
        {
            lifetime = transaction.KeyExpireAsync(target, ChunkedWriteOrphanLifetime, CommandFlags.DemandMaster);
        }
        await CommitAsync(transaction, "A chunk", target, written, lifetime).ConfigureAwait(false);
    }
}
