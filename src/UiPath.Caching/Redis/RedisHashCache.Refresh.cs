namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    private static void QueueExpirationUpdate(ITransaction transaction, RedisKey redisKey, DateTimeOffset expiration)
    {
        if (expiration != DateTimeOffset.MaxValue)
        {
            transaction.KeyExpireAsync(redisKey, expiration.UtcDateTime, CommandFlags.DemandMaster | CommandFlags.FireAndForget).Forget();
            return;
        }
        transaction.KeyPersistAsync(redisKey, CommandFlags.DemandMaster | CommandFlags.FireAndForget).Forget();
    }

    private async ValueTask<bool> RefreshWithOptionsAsync<T>(CacheKey cacheKey, HashCacheEntryOptions options, CachePolicy? policy, ICacheValueSerializer? writer, CancellationToken token)
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
            if (expiration < now)
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
                await EnsureConnectedAsync(_write, token).ConfigureAwait(false);
                var transaction = Database.CreateTransaction();
                using (QueueMetadataWrite(transaction, Logged(cacheKey, redisKey, typeof(T)), redisKey, options.Metadata, writer))
                {
                    QueueExpirationUpdate(transaction, redisKey, expiration);

                    ret = await _write.ExecuteAsync(static (s, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return s.ExecuteAsync(CommandFlags.DemandMaster).AsValueTask();
                    },
                    transaction,
                    default,
                    token).ConfigureAwait(false);
                }

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
