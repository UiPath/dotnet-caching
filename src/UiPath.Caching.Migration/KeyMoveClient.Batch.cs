using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveClient<T>
{
    /// <summary>Probes N with <c>MGET</c> per slot. Reverse read-through probes the master directly. Other reads check replica misses on the master. Each promotion captures its own fence before reading the source value.</summary>
    public async ValueTask<IReadOnlyDictionary<string, PathRead<T>>> ReadManyAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PathRead<T>>(keys.Count, StringComparer.Ordinal);
        var plan = PlanFor(write: false);
        if (plan.Mode is KeyMoveMode.Plain or KeyMoveMode.Preparing or KeyMoveMode.Switching || Data is not { } data)
        {
            var each = await Task.WhenAll(keys.Select(k => ReadAsync(k, cancellationToken).AsTask())).ConfigureAwait(false);
            for (var i = 0; i < keys.Count; i++)
            {
                result[keys[i]] = each[i];
            }

            return result;
        }

        var reverse = KeyMovePlanning.PromotesReverse(plan);
        var missing = keys.Distinct(StringComparer.Ordinal).ToList();
        missing = await ProbeBatchAsync(data, missing, result, masterOnly: reverse, cancellationToken).ConfigureAwait(false);

        if (reverse)
        {
            await Task.WhenAll(result.Where(entry => entry.Value.Kind == ReadOutcome.Found)
                .Select(entry => PromoteReverseAsync(entry.Key, plan, cancellationToken).AsTask())).ConfigureAwait(false);
        }

        if (missing.Count == 0 || plan.Mode == KeyMoveMode.Done)
        {
            foreach (var key in missing)
            {
                result[key] = PathRead<T>.Of(ReadOutcome.Absent);
            }

            return result;
        }

        if (plan.Mode == KeyMoveMode.Copying)
        {
            await ReadMissingWithPromotionAsync(missing, plan, result, cancellationToken).ConfigureAwait(false);
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

    private async Task<List<string>> ProbeBatchAsync(KeyMoveData data, List<string> missing, Dictionary<string, PathRead<T>> result, bool masterOnly, CancellationToken cancellationToken)
    {
        bool[] passes = masterOnly ? [false] : [true, false];
        foreach (var replica in passes)
        {
            if (missing.Count == 0)
            {
                break;
            }

            await At(replica ? SchedulePointStep.ReadDestination : SchedulePointStep.ReadDestinationOnMaster).ConfigureAwait(false);
            var (values, failed) = await MultiGetAsync(data, missing, replica, cancellationToken).ConfigureAwait(false);

            // A slot whose read failed makes only its own keys unavailable; the others go on.
            if (failed.Count > 0)
            {
                foreach (var i in failed)
                {
                    result[missing[i]] = PathRead<T>.Of(ReadOutcome.Unavailable);
                }

                values = [.. values.Where((_, i) => !failed.Contains(i))];
                missing = [.. missing.Where((_, i) => !failed.Contains(i))];
            }

            var hits = Enumerable.Range(0, missing.Count).Where(i => !values[i].IsNull).ToArray();
            var lives = await ReadBatchLifetimesAsync(data, missing, hits, replica, cancellationToken).ConfigureAwait(false);
            var still = RecordBatchHits(missing, values, hits.Zip(lives).ToDictionary(p => p.First, p => p.Second), result);
            missing = still;
        }

        return missing;
    }

    // A key that expires between MGET and PTTL is a miss.
    // The remaining life of each hit; null for a hit whose life could not be read, which is never taken as no expiry.
    private async Task<long?[]> ReadBatchLifetimesAsync(KeyMoveData data, List<string> missing, int[] hits, bool replica, CancellationToken cancellationToken)
    {
        await At(SchedulePointStep.ReadLife).ConfigureAwait(false);
        var flags = replica ? CommandFlags.PreferReplica : Master;
        return await Task.WhenAll(hits.Select(async i =>
        {
            try
            {
                return (long?)(long)await data.Db.ExecuteAsync("PTTL", [Key(Native(missing[i]))], flags).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        })).ConfigureAwait(false);
    }

    private List<string> RecordBatchHits(List<string> missing, RedisValue[] values, Dictionary<int, long?> life, Dictionary<string, PathRead<T>> result)
    {
        var still = new List<string>();
        for (var i = 0; i < missing.Count; i++)
        {
            var read = BatchHit(values[i], values[i].IsNull ? -2 : life[i]);
            if (read.FallsThrough)
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
    // A hit read with its life: a life that could not be read makes it unavailable, and a key gone since the MGET makes it absent.
    private PathRead<T> BatchHit(RedisValue value, long? life) => life switch
    {
        null => PathRead<T>.Of(ReadOutcome.Unavailable),
        -2 => PathRead<T>.Of(ReadOutcome.Absent),
        _ => Interpret(new KeyMoveSnap(ReadOutcome.Found, (byte[]?)value ?? [], Math.Max(-1, life.Value), _p.Clock.GetUtcNow()), _p.Native, authoritativeNull: true),
    };

    // One MGET per slot; the indexes of the keys whose slot could not be read come back apart.
    private async Task<(RedisValue[] Values, HashSet<int> Failed)> MultiGetAsync(KeyMoveData data, List<string> keys, bool replica, CancellationToken cancellationToken)
    {
        var flags = replica ? CommandFlags.PreferReplica : Master;
        var values = new RedisValue[keys.Count];
        var failed = new HashSet<int>();
        var groups = keys.Select((key, index) => (key, index)).GroupBy(p => data.Names.SlotOf(Native(p.key))).ToList();
        var reads = groups.Select(async group =>
        {
            var members = group.ToArray();
            try
            {
                var got = await data.Db.StringGetAsync([.. members.Select(m => Key(Native(m.key)))], flags).ConfigureAwait(false);
                for (var i = 0; i < members.Length; i++)
                {
                    values[members[i].index] = got[i];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                lock (failed)
                {
                    failed.UnionWith(members.Select(m => m.index));
                }
            }
        });
        await Task.WhenAll(reads).ConfigureAwait(false);
        return (values, failed);
    }

    private async Task ReadMissingWithPromotionAsync(List<string> missing, KeyMovePlan plan, Dictionary<string, PathRead<T>> result, CancellationToken cancellationToken)
    {
        // Only the source existence probe is batched, using one pipeline of EXISTS on the masters.
        var identity = plan.Identity;
        var candidates = missing.Where(k => !_hints.Has(k, identity, MoveSide.Legacy)).ToList();
        foreach (var hinted in missing.Except(candidates))
        {
            result[hinted] = PathRead<T>.Of(ReadOutcome.Absent);
        }

        await At(SchedulePointStep.ProbeSource).ConfigureAwait(false);
        var probes = await Task.WhenAll(candidates.Select(async k =>
        {
            try
            {
                return await Source.ExistsAsync(k, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        })).ConfigureAwait(false);

        var work = ResolveSourceProbes(candidates, probes, plan, identity, result, cancellationToken);
        foreach (var (key, read) in await Task.WhenAll(work).ConfigureAwait(false))
        {
            result[key] = read;
        }
    }

    private List<Task<(string Key, PathRead<T> Read)>> ResolveSourceProbes(List<string> candidates, bool?[] probes, KeyMovePlan plan, string identity, Dictionary<string, PathRead<T>> result, CancellationToken cancellationToken)
    {
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

        return work;
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
