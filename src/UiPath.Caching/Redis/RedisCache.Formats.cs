using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache
{
    private ValueFormat FormatFor(CachePolicy? policy, bool forWrite, bool alsoReads = false) => _formats.For(policy, forWrite, alsoReads);

    /// <summary>A read found a format no registered serializer owns: counted, and the key dropped if it still holds the bytes that were read, so the caller regenerates it. Best effort, like any remove.</summary>
    private ValueTask<bool> RemoveUnreadableAsync<T>(RedisKey redisKey, RedisValue read, int formatId, CancellationToken token)
    {
        TrackUnreadable(formatId, Name);
        return RemoveIfUnchangedAsync<T>(redisKey, read, token);
    }

    /// <summary>Never throws what <paramref name="format"/> handles, so a late cancellation leaves the batch's misses as they are.</summary>
    private async ValueTask RemoveUnreadableAsync<T, TFormat>(List<(RedisKey Key, RedisValue Read, int FormatId)> unreadable, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        foreach (var (_, _, formatId) in unreadable)
        {
            TrackUnreadable(formatId, Name);
        }

        try
        {
            await Task.WhenAll(unreadable.Select(u => RemoveIfUnchangedAsync<T>(u.Key, u.Read, token).AsTask())).ConfigureAwait(false);
        }
        catch (Exception ex) when (format.Handles(ex))
        {
            LogRedisCacheException(ex);
        }
    }

    // A transaction per key, so a key that changed fails alone and stays on its own slot; the condition runs on the master, so a lagging replica's read cannot delete a newer value.
    private async ValueTask<bool> RemoveIfUnchangedAsync<T>(RedisKey redisKey, RedisValue read, CancellationToken token)
    {
        bool ret = default;
        token.ThrowIfCancellationRequested();
        NoteWritten(redisKey);
        var operation = StartOperation<T>(nameof(RemoveAsync));
        try
        {
            ret = await _write.ExecuteAsync(static (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                var transaction = s.Self.Database.CreateTransaction(asyncState: null);
                // The server-side compare-and-delete needs Redis 8.4; this library supports 6.
#pragma warning disable SER301
                transaction.AddCondition(Condition.StringEqual(s.Key, s.Read));
                transaction.KeyDeleteAsync(s.Key, CommandFlags.DemandMaster).Forget();
#pragma warning restore SER301
                return transaction.ExecuteAsync(CommandFlags.DemandMaster).AsValueTask();
            },
            (Self: this, Key: redisKey, Read: read),
            default,
            token).ConfigureAwait(false);
            operation.Stop();
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
}
