using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

// Read back after an unconfirmed conditioned write to distinguish an applied transaction from a failed condition.
internal sealed class ServerRecordWriter(ServerBarrier barrier, SideCaps caps, Func<RedisKey, Task<HashEntry[]>> readMaster, Func<LeaseFence, Task<bool>>? holdsFence = null)
{
    public Task<RecordWrite> RunAsync(RecordOp op, bool wayBack)
    {
        ArgumentNullException.ThrowIfNull(op);
        return caps.Scripts ? ScriptAsync([op], wayBack, many: false) : ConditionAsync([op], wayBack, many: false);
    }

    public Task<RecordWrite> RunManyAsync(IReadOnlyList<RecordOp> ops, bool wayBack)
    {
        ArgumentNullException.ThrowIfNull(ops);
        if (ops.Count == 0)
        {
            throw new ArgumentException("At least one write is needed.", nameof(ops));
        }

        return caps.Scripts ? ScriptAsync(ops, wayBack, many: true) : ConditionAsync(ops, wayBack, many: true);
    }

    private static async Task<long> CodeOfAsync(Task<RedisResult>? reply)
    {
        if (reply is null)
        {
            return RecordWrite.NoCode;
        }

        try
        {
            return (long)await reply.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or OperationCanceledException or InvalidCastException)
        {
            return RecordWrite.NoCode;
        }
    }

    private static RecordWrite Combine(BarrierOutcome outcome, IReadOnlyList<long> codes, Exception? uncertain, int attempts) =>
        new(outcome, codes.Contains(RecordWrite.NoCode) ? RecordWrite.NoCode : codes.Min(), uncertain, attempts);

    private static bool SameRecord(HashEntry[] a, HashEntry[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var left = a.ToDictionary(static e => e.Name.ToString(), static e => (string?)e.Value);
        return b.All(e => left.TryGetValue(e.Name.ToString(), out var value) && value == (string?)e.Value);
    }

    private static List<KeyedWrite> ConditionQueues(IReadOnlyList<RecordOp> ops, ConditionPlan[] plans)
    {
        var queues = new List<KeyedWrite>(plans.Length);
        for (var i = 0; i < plans.Length; i++)
        {
            var plan = plans[i];
            if (plan.Writes is not { } writes)
            {
                continue;
            }

            queues.Add(new KeyedWrite(ops[i].Key, t =>
            {
                foreach (var condition in plan.Conditions)
                {
                    t.AddCondition(condition);
                }

                writes(t);
            }));
        }

        return queues;
    }

    private static RecordWrite? ReadBack(
        IReadOnlyList<RecordOp> ops,
        ConditionPlan[] plans,
        HashEntry[][] held,
        HashEntry[][] after,
        long[] codes,
        BarrierOutcome outcome,
        Exception? uncertain)
    {
        var applied = true;
        var unchanged = true;
        for (var i = 0; i < ops.Count; i++)
        {
            applied &= plans[i].Writes is null || ops[i].Applied(after[i]);
            unchanged &= SameRecord(held[i], after[i]);
        }

        // A possibly committed EXEC must never be replayed after an error.
        if (!applied && !unchanged && uncertain is null)
        {
            return null;
        }

        for (var i = 0; i < codes.Length; i++)
        {
            if (plans[i].Writes is not null && !ops[i].Applied(after[i]))
            {
                codes[i] = RecordWrite.NoCode;
            }
        }

        return Combine(outcome, codes, uncertain, 1);
    }

    private async Task<RecordWrite> ScriptAsync(IReadOnlyList<RecordOp> ops, bool wayBack, bool many)
    {
        var replies = new Task<RedisResult>?[ops.Count];
        var attempts = 0;
        var queues = new KeyedWrite[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            var index = i;
            queues[i] = new KeyedWrite(ops[index].Key, t =>
            {
                if (index == 0)
                {
                    Interlocked.Increment(ref attempts);
                }

                replies[index] = ops[index].Script(t);
            });
        }

        var before = barrier.LastError;
        var outcome = many
            ? await barrier.WriteManyByKeyAndWaitAsync(queues, wayBack).ConfigureAwait(false)
            : await barrier.WriteAndWaitAsync(queues[0].Queue, wayBack).ConfigureAwait(false);
        var codes = new long[ops.Count];
        for (var i = 0; i < codes.Length; i++)
        {
            codes[i] = await CodeOfAsync(replies[i]).ConfigureAwait(false);
        }

        return Combine(outcome, codes, UncertainSince(before), Volatile.Read(ref attempts)) with { Proof = barrier.LastProof };
    }

    private async Task<RecordWrite> ConditionAsync(IReadOnlyList<RecordOp> ops, bool wayBack, bool many)
    {
        var before = barrier.LastError;
        var outcome = BarrierOutcome.NotConfirmed;
        for (var round = 0; round < MoveScripts.MaxWatchAborts; round++)
        {
            var held = await ReadAllAsync(ops).ConfigureAwait(false);
            var plans = await PlanWritesAsync(ops, held).ConfigureAwait(false);
            if (plans is null)
            {
                return new RecordWrite(BarrierOutcome.Confirmed, MoveScripts.LeaseNotHeld, null);
            }

            if (plans.All(static p => p.Writes is null))
            {
                // Nothing to write: the codes are refusals, and there is nothing to confirm.
                return Combine(BarrierOutcome.Confirmed, [.. plans.Select(static p => p.Code)], null, 0);
            }

            var queues = ConditionQueues(ops, plans);
            outcome = many
                ? await barrier.WriteManyByKeyAndWaitAsync(queues, wayBack).ConfigureAwait(false)
                : await barrier.WriteAndWaitAsync(queues[0].Queue, wayBack).ConfigureAwait(false);
            var codes = plans.Select(static p => p.Code).ToArray();
            if (outcome is BarrierOutcome.Confirmed or BarrierOutcome.NoReplica)
            {
                return Combine(outcome, codes, UncertainSince(before), 1) with { Proof = barrier.LastProof };
            }

            var after = await ReadAllAsync(ops).ConfigureAwait(false);
            var uncertain = UncertainSince(before);
            if (ReadBack(ops, plans, held, after, codes, outcome, uncertain) is { } result)
            {
                return result;
            }
        }

        return new RecordWrite(outcome, RecordWrite.NoCode, UncertainSince(before), MoveScripts.MaxWatchAborts);
    }

    private async Task<ConditionPlan[]?> PlanWritesAsync(IReadOnlyList<RecordOp> ops, HashEntry[][] held)
    {
        var plans = new ConditionPlan[ops.Count];
        for (var i = 0; i < plans.Length; i++)
        {
            if (ops[i].Fence is { } fence && holdsFence is not null && !await holdsFence(fence).ConfigureAwait(false))
            {
                return null;
            }

            plans[i] = ops[i].Plan(held[i]);
        }

        return plans;
    }

    private Task<HashEntry[][]> ReadAllAsync(IReadOnlyList<RecordOp> ops) =>
        Task.WhenAll(ops.Select(op => readMaster(op.Key)));

    private Exception? UncertainSince(Exception? before) => barrier.LastError is { } now && !ReferenceEquals(now, before) ? now : null;
}
