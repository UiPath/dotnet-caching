namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    /// <param name="commands">Given for a traced operation: the commands are then answered, not fired and forgotten, and their tasks are collected so that a refusal reads as a failure.</param>
    private static void QueueExpirationUpdate(ITransaction transaction, RedisKey redisKey, DateTimeOffset expiration, List<Task>? commands = null)
    {
        var flags = commands is null ? CommandFlags.DemandMaster | CommandFlags.FireAndForget : CommandFlags.DemandMaster;
        var update = expiration != DateTimeOffset.MaxValue
            ? transaction.KeyExpireAsync(redisKey, expiration.UtcDateTime, flags)
            : transaction.KeyPersistAsync(redisKey, flags);
        update.Forget();
        commands?.Add(update);
    }

    /// <summary>Runs the transaction; a traced one is answered only when its commands succeeded too, because EXEC commits even when one of them fails, for example on a key of another type.</summary>
    private static async ValueTask<bool> ExecuteTracedAsync(ITransaction transaction, SendTrace? trace, List<Task> commands, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        trace?.MarkSent();
        bool executed;
        try
        {
            executed = await transaction.ExecuteAsync(CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (RedisServerException) when (trace is not null)
        {
            // The server answered with an error, to a condition for example: nothing was applied.
            trace.MarkAnswered(false);
            return false;
        }

        if (trace is null)
        {
            return executed;
        }

        if (executed && !await SucceededAsync(commands).ConfigureAwait(false))
        {
            trace.MarkRanWithFault();
        }
        else
        {
            trace.MarkAnswered(executed);
        }

        return executed;
    }

    private static async ValueTask<bool> SucceededAsync(List<Task> commands)
    {
        try
        {
            await Task.WhenAll(commands).ConfigureAwait(false);
            return true;
        }
        catch (RedisException)
        {
            return false;
        }
    }

    private ValueTask<bool> CommitTracedAsync(ITransaction transaction, SendTrace? trace, List<Task> commands, CancellationToken token) =>
        _write.ExecuteAsync(static (s, token) => ExecuteTracedAsync(s.Transaction, s.Trace, s.Commands, token), (Transaction: transaction, Trace: trace, Commands: commands), default, token);

    private async ValueTask<bool> RefreshWithOptionsAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, ICacheValueSerializer? writer, CancellationToken token, SendTrace? trace = null, bool requireKey = false, bool expiryOnly = false)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        var expiration = GetExpiration(options, policy);
        var now = Clock.GetUtcNow();
        var ret = false;
        NoteWritten(redisKey);
        var operation = StartOperation<T>(nameof(RefreshAsync));
        try
        {
            if (expiration < now && !requireKey)
            {
                ret = await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster).AsValueTask();
                },
                (Self: this, Key: redisKey),
                default,
                token).ConfigureAwait(false);
            }
            else
            {
                ret = await RefreshInTransactionAsync<T>(cacheKey, redisKey, options, expiration, now, writer, trace, requireKey, expiryOnly, token).ConfigureAwait(false);
                if (!ret)
                {
                    LogRedisTransactionFailed();
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

    private async ValueTask<bool> RefreshInTransactionAsync<T>(CacheKey cacheKey, RedisKey redisKey, HashCacheEntryOptions options, DateTimeOffset expiration, DateTimeOffset now, ICacheValueSerializer? writer, SendTrace? trace, bool requireKey, bool expiryOnly, CancellationToken token)
    {
        await EnsureConnectedAsync(_write, token).ConfigureAwait(false);
        var transaction = Database.CreateTransaction();
        if (requireKey)
        {
            // A refresh that reports acts on a hash that is there, and on nothing otherwise: the library's own would make a key of the metadata alone. The condition is held under WATCH, so the key cannot become another type, or go, between it and the commit.
            transaction.AddCondition(Condition.HashLengthGreaterThan(redisKey, 0));
        }

        var commands = new List<Task>();
        if (expiration < now)
        {
            var deleted = transaction.KeyDeleteAsync(redisKey, CommandFlags.DemandMaster);
            deleted.Forget();
            commands.Add(deleted);
            return await CommitTracedAsync(transaction, trace, commands, token).ConfigureAwait(false);
        }

        using (expiryOnly ? default : QueueMetadataWrite(transaction, Logged(cacheKey, redisKey, typeof(T)), redisKey, options.Metadata, writer, commands))
        {
            QueueExpirationUpdate(transaction, redisKey, expiration, trace is null ? null : commands);
            return await CommitTracedAsync(transaction, trace, commands, token).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> RefreshCoreAsync<T>(CacheKey cacheKey, DateTimeOffset localExpiration, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        var redisKey = ToRedisKey(cacheKey, token);
        LogRefreshingKey(Logged(cacheKey, redisKey, typeof(T)), localExpiration);
        var ret = false;
        NoteWritten(redisKey);
        var operation = StartOperation<T>();
        try
        {
            ret = localExpiration != DateTimeOffset.MaxValue
                ? await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyExpireAsync(s.Key, s.LocalExpiration.UtcDateTime, s.Self.RefreshFlags).AsValueTask();
                },
                (Self: this, Key: redisKey, LocalExpiration: localExpiration),
                default,
                token).ConfigureAwait(false)
                : await _write.ExecuteAsync(static (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return s.Self.Database.KeyPersistAsync(s.Key, s.Self.RefreshFlags).AsValueTask();
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
            LogRedisHashCacheException(ex);
        }
        finally
        {
            NoteWritten(redisKey);
            operation.Track(ret);
        }

        return ret;
    }
}
