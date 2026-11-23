using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveClient<T>
{
    /// <summary>Probes with <c>MGET N</c> per slot and checks replica misses on the master. Remaining misses follow read-through promotion per key. Only the existence probe is batched. Each source value is read after its key's capture.</summary>
    public async ValueTask<IReadOnlyDictionary<string, PathRead<T>>> ReadManyAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PathRead<T>>(keys.Count, StringComparer.Ordinal);
        var plan = PlanFor(write: false);
        if (plan.Mode is KeyMoveMode.Plain or KeyMoveMode.S1 or KeyMoveMode.S2 || Data is not { } data)
        {
            var each = await Task.WhenAll(keys.Select(k => ReadAsync(k, cancellationToken).AsTask())).ConfigureAwait(false);
            for (var i = 0; i < keys.Count; i++)
            {
                result[keys[i]] = each[i];
            }

            return result;
        }

        var missing = keys.Distinct(StringComparer.Ordinal).ToList();
        missing = await ProbeBatchAsync(data, missing, result, cancellationToken).ConfigureAwait(false);

        if (missing.Count == 0 || plan.Mode == KeyMoveMode.S5)
        {
            foreach (var key in missing)
            {
                result[key] = PathRead<T>.Of(ReadOutcome.Absent);
            }

            return result;
        }

        if (plan.Mode == KeyMoveMode.S3)
        {
            await ReadMissingWithPromotionAsync(missing, plan, data, result, cancellationToken).ConfigureAwait(false);
            return result;
        }

        var tails = await Task.WhenAll(missing.Select(k => ReadManagedAsync(k, plan, cancellationToken).AsTask())).ConfigureAwait(false);
        for (var i = 0; i < missing.Count; i++)
        {
            result[missing[i]] = tails[i];
        }

        return result;
    }

    /// <summary>Keys run concurrently, with each key's write steps running in order.</summary>
    public async ValueTask<WriteResult[]> SetManyAsync(IReadOnlyList<(string Key, T Value, DateTimeOffset? Deadline)> entries, CancellationToken cancellationToken) =>
        await Task.WhenAll(entries.Select(e => Reported(SetAsync(e.Key, e.Value, e.Deadline, cancellationToken)))).ConfigureAwait(false);

    public async ValueTask<WriteResult[]> RemoveManyAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        await Task.WhenAll(keys.Select(k => Reported(RemoveAsync(k, cancellationToken)))).ConfigureAwait(false);

    private static async Task<WriteResult> Reported(ValueTask<WriteResult> write)
    {
        try
        {
            return await write.ConfigureAwait(false);
        }
        catch (CacheUnavailableException)
        {
            // A key the form closes is reported failed, as NotSent.
            return WriteOutcome.NotSent;
        }
    }

    private async Task<List<string>> ProbeBatchAsync(KeyMoveData data, List<string> missing, Dictionary<string, PathRead<T>> result, CancellationToken cancellationToken)
    {
        foreach (var replica in new[] { true, false })
        {
            if (missing.Count == 0)
            {
                break;
            }

            await At(replica ? SchedulePointStep.ReadDestination : SchedulePointStep.ReadDestinationOnMaster).ConfigureAwait(false);
            RedisValue[] values;
            try
            {
                values = await MultiGetAsync(data, missing, replica).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                foreach (var key in missing)
                {
                    result[key] = PathRead<T>.Of(ReadOutcome.Unavailable);
                }

                return [];
            }

            var hits = Enumerable.Range(0, missing.Count).Where(i => !values[i].IsNull).ToArray();
            var lives = await ReadBatchLifetimesAsync(data, missing, hits, replica, cancellationToken).ConfigureAwait(false);
            var still = RecordBatchHits(missing, values, hits.Zip(lives).ToDictionary(p => p.First, p => p.Second), result);
            missing = still;
        }

        return missing;
    }

    // A key that expires between MGET and PTTL is a miss. If PTTL fails, the local cap bounds any cached hit.
    private async Task<long[]> ReadBatchLifetimesAsync(KeyMoveData data, List<string> missing, int[] hits, bool replica, CancellationToken cancellationToken)
    {
        try
        {
            await At(SchedulePointStep.ReadLife).ConfigureAwait(false);
            var flags = replica ? CommandFlags.PreferReplica : Master;
            return await Task.WhenAll(hits.Select(async i => (long)await data.Db.ExecuteAsync("PTTL", [Key(Native(missing[i]))], flags).ConfigureAwait(false))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return [.. hits.Select(_ => -1L)];
        }
    }

    private List<string> RecordBatchHits(List<string> missing, RedisValue[] values, Dictionary<int, long> life, Dictionary<string, PathRead<T>> result)
    {
        var still = new List<string>();
        for (var i = 0; i < missing.Count; i++)
        {
            var read = values[i].IsNull || life[i] == -2
                ? PathRead<T>.Of(ReadOutcome.Absent)
                : Interpret(new KeyMoveSnap(ReadOutcome.Found, (byte[]?)values[i] ?? [], Math.Max(-1, life[i]), _p.Clock.GetUtcNow()), _p.Native);
            if (read.Kind == ReadOutcome.Absent)
            {
                still.Add(missing[i]);
            }
            else
            {
                result[missing[i]] = read;
            }
        }

        return still;
    }

    // One MGET per slot, so a cluster never sees a command that spans slots.
    private async Task<RedisValue[]> MultiGetAsync(KeyMoveData data, List<string> keys, bool replica)
    {
        var flags = replica ? CommandFlags.PreferReplica : Master;
        var values = new RedisValue[keys.Count];
        var groups = keys.Select((key, index) => (key, index)).GroupBy(p => data.Names.SlotOf(Native(p.key))).ToList();
        var reads = groups.Select(async group =>
        {
            var members = group.ToArray();
            var got = await data.Db.StringGetAsync([.. members.Select(m => Key(Native(m.key)))], flags).ConfigureAwait(false);
            for (var i = 0; i < members.Length; i++)
            {
                values[members[i].index] = got[i];
            }
        });
        await Task.WhenAll(reads).ConfigureAwait(false);
        return values;
    }

    private async Task ReadMissingWithPromotionAsync(List<string> missing, KeyMovePlan plan, KeyMoveData data, Dictionary<string, PathRead<T>> result, CancellationToken cancellationToken)
    {
        // Only the source existence probe is batched, using one pipeline of EXISTS on the masters.
        var identity = plan.Identity;
        var candidates = missing.Where(k => !_hints.Has(k, identity)).ToList();
        foreach (var hinted in missing.Except(candidates))
        {
            result[hinted] = PathRead<T>.Of(ReadOutcome.Absent);
        }

        await At(SchedulePointStep.ProbeSource).ConfigureAwait(false);
        var probes = await Task.WhenAll(candidates.Select(async k =>
        {
            try
            {
                return await data.Db.KeyExistsAsync(Key(InPlace(k)), Master).ConfigureAwait(false) ? (bool?)true : false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        })).ConfigureAwait(false);

        var work = new List<Task<(string Key, PathRead<T> Read)>>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var key = candidates[i];
            if (probes[i] is null)
            {
                result[key] = PathRead<T>.Of(ReadOutcome.Unavailable);
            }
            else if (probes[i] == false)
            {
                work.Add(MissOfAsync(key, identity, cancellationToken));
            }
            else
            {
                work.Add(PromoteKeyAsync(key, plan, cancellationToken));
            }
        }

        foreach (var (key, read) in await Task.WhenAll(work).ConfigureAwait(false))
        {
            result[key] = read;
        }
    }

    private async Task<(string Key, PathRead<T> Read)> MissOfAsync(string key, string identity, CancellationToken cancellationToken) =>
        (key, await MissAsync(key, Native(key), identity, cancellationToken).ConfigureAwait(false));

    // Capture each key before taking its source snapshot.
    private async Task<(string Key, PathRead<T> Read)> PromoteKeyAsync(string key, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        var step = await PromoteAsync(key, plan, cancellationToken, probed: true).ConfigureAwait(false);
        return (key, step.Rerun ? await ReadManagedAsync(key, PlanFor(write: false), cancellationToken).ConfigureAwait(false) : step.Read);
    }
}
