using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

internal sealed partial class RedisHashCache
{
    // One claim per field, so a hash read a field at a time migrates field by field rather than one a window, and the rate counts values.
    private void OfferRewrites<T>(RedisKey key, FieldRewrites? rewrites)
    {
        if (rewrites is null || _migration is not { } migration)
        {
            return;
        }

        List<(RedisValue Field, byte[] Stored)> claimed = [];
        var bytes = 0;
        foreach (var (field, stored) in rewrites.Fields)
        {
            // What one rewrite carries is bounded, but only by the fields that are taken: those not offered lately (a lossy one waits an hour) do not use it up, so a big hash goes on.
            if (bytes + stored.Length > ValueMigration.MaxJobBytes)
            {
                continue;
            }

            var result = migration.Migrator.TryClaim(this, key.Append((RedisKey)("\0" + field)), MetricCache, stored.Length);
            if (result == FormatMigrator.ClaimResult.Claimed)
            {
                claimed.Add((field, stored));
                bytes += stored.Length;
            }
            else if (result == FormatMigrator.ClaimResult.Refused)
            {
                // The rate, the queue or the byte budget holds for the fields after this one as well; asking each would only take the gate for the same answer.
                break;
            }
        }

        if (claimed.Count == 0)
        {
            return;
        }

        migration.Migrator.EnqueueClaimed(
            MetricCache,
            [.. claimed.Select(c => new FormatMigrator.OfferKey(this, key.Append((RedisKey)("\0" + c.Field))))],
            bytes,
            (Self: this, Key: key, Claimed: claimed, Target: rewrites.Target, Migration: migration),
            static s =>
            {
                var originals = s.Claimed.ConvertAll(f => (f.Field, Stored: (byte[])f.Stored.Clone()));
                var (self, key, target, migration) = (s.Self, s.Key, s.Target, s.Migration);
                return token => self.RewriteFieldsAsync<T>(key, originals, target, migration, token);
            });
    }

    private void RewriteScriptsRefused(Exception ex)
    {
        RewriteScriptRefused();
        LogRewriteScriptRefused(ex);
    }

    private async ValueTask<RewriteTally> RewriteFieldsAsync<T>(RedisKey key, List<(RedisValue Field, byte[] Stored)> originals, ICacheValueSerializer target, ValueMigration migration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!migration.Allows(target))
        {
            return new RewriteTally(0, 0, 0, originals.Count);
        }

        var payloads = new List<SerializedPayload>(originals.Count);
        var fields = new List<(RedisValue Field, ReadOnlyMemory<byte> Expected, ReadOnlyMemory<byte> Value)>(originals.Count);
        List<int> lossyAt = [];
        try
        {
            for (var i = 0; i < originals.Count; i++)
            {
                var (field, stored) = originals[i];
                if (migration.TryReencode<T>(stored, target, out var payload))
                {
                    payloads.Add(payload);
                    fields.Add((field, stored, payload.Memory));
                }
                else
                {
                    lossyAt.Add(i);
                }
            }

            var applied = 0;
            var held = 0;
            for (var start = 0; start < fields.Count; start += _fieldBatchSize)
            {
                var size = Math.Min(_fieldBatchSize, fields.Count - start);
                // A batch that waited behind the others may find the node list has moved on: what is not yet written is held.
                if (!migration.Allows(target))
                {
                    held = fields.Count - start;
                    break;
                }

                var batch = fields.GetRange(start, size);
                var count = await _write.ExecuteAsync(static async (s, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return (int?)await FormatRewriteScripts.RewriteHashFieldsAsync(s.Self.Database, s.Key, s.Batch, s.Self.RewriteScriptsOn, s.Self._rewriteScriptRefused).ConfigureAwait(false);
                },
                (Self: this, Key: key, Batch: batch),
                default(int?),
                token).ConfigureAwait(false);
                applied += count ?? throw new InvalidOperationException("The write pipeline gave up on the rewrite.");
            }

            return new RewriteTally(applied, fields.Count - applied - held, lossyAt.Count, held) { LossyAt = lossyAt };
        }
        finally
        {
            foreach (var payload in payloads)
            {
                payload.Dispose();
            }
        }
    }

    private sealed class FieldRewrites(ICacheValueSerializer target)
    {
        public ICacheValueSerializer Target => target;

        public List<(RedisValue Field, byte[] Stored)> Fields { get; } = [];

        // A field that is not a user field (a legacy _word_ one) would fail the whole batch, so it is left.
        public void TryAdd(RedisValue field, byte[] stored)
        {
            if (!KnownFieldNames.IsReserved(field.ToString()))
            {
                Fields.Add((field, stored));
            }
        }
    }
}
