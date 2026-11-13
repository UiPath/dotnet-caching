using UiPath.Caching.Policies;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisCache
{
    private ValueFormat FormatFor(CachePolicy? policy, bool forWrite, bool alsoReads = false) => _formats.For(policy, forWrite, alsoReads);

    private void TrackJsonWrite<TFormat>(TFormat format)
        where TFormat : struct, IValueFormat
    {
        if (format.Readable is not null)
        {
            TrackSerializer(Metrics.SerializerWrite, CacheValueFormats.JsonName, Name);
        }
    }

    // The job reads its own value from the bytes it was given, so nothing the caller holds is queued; the queue refuses what it saw lately, what the rate, the queue and the byte budget do not allow, and the copy is made only once the offer is taken.
    private void OfferRewrite<T>(RedisKey key, byte[] stored, ICacheValueSerializer target, ValueMigration migration) =>
        migration.Migrator.TryEnqueue(
            this,
            key,
            Name,
            stored.Length,
            (Self: this, Key: key, Stored: stored, Target: target, Migration: migration),
            static s =>
            {
                // The read's buffer may be one a connector decorator or a pooling client reuses; what the job compares and writes over must not change under it.
                var original = (byte[])s.Stored.Clone();
                var (self, key, target, migration) = (s.Self, s.Key, s.Target, s.Migration);
                return token => self.RewriteStoredAsync<T>(key, original, target, migration, token);
            });

    private void RewriteScriptsRefused(Exception ex)
    {
        RewriteScriptRefused();
        LogRewriteScriptRefused(ex);
    }

    private async ValueTask<RewriteTally> RewriteStoredAsync<T>(RedisKey key, byte[] original, ICacheValueSerializer target, ValueMigration migration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The queue may have held this a while: what the node list allows is asked again, here and right before the write.
        if (!migration.Allows(target))
        {
            return RewriteTally.Of(RewriteOutcome.Held);
        }

        if (!migration.TryReencode<T>(original, target, out var payload))
        {
            return RewriteTally.Of(RewriteOutcome.Lossy);
        }

        using (payload)
        {
            if (!migration.Allows(target))
            {
                return RewriteTally.Of(RewriteOutcome.Held);
            }

            var applied = await _write.ExecuteAsync(static async (s, token) =>
            {
                token.ThrowIfCancellationRequested();
                return (bool?)await FormatRewriteScripts.RewriteStringAsync(s.Self.Database, s.Key, s.Original, s.Payload, s.Self.RewriteScriptsOn, s.Self._rewriteScriptRefused).ConfigureAwait(false);
            },
            (Self: this, Key: key, Original: original, Payload: payload.Memory),
            default(bool?),
            token).ConfigureAwait(false);
            return applied switch
            {
                true => RewriteTally.Of(RewriteOutcome.Done),
                false => RewriteTally.Of(RewriteOutcome.Changed),
                null => throw new InvalidOperationException("The write pipeline gave up on the rewrite."),
            };
        }
    }

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
