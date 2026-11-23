using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    internal static Task<RedisResult> QueueTransit(ITransaction transaction, RedisKey state, StateExpectation expected, StateTarget target, LeaseFence? fence)
    {
        var (args, _, _) = TransitArguments(expected, target, fence);
        return transaction.ScriptEvaluateAsync(TransitScript, KeysOf(state, fence), args, Master);
    }

    internal static Task<RedisResult> QueueCommitBegin(ITransaction transaction, RedisKey state, string id, LeaseFence? fence) =>
        transaction.ScriptEvaluateAsync(CommitBeginScript, KeysOf(state, fence), [id, fence?.Token ?? string.Empty], Master);

    internal static Task<RedisResult> QueueDurable(ITransaction transaction, RedisKey state, string id, string release) =>
        transaction.ScriptEvaluateAsync(DurableScript, [state], [id, release], Master);

    internal static Task<RedisResult> QueueControlSync(ITransaction transaction, RedisKey control, ControlRecord record) =>
        transaction.ScriptEvaluateAsync(CtlSyncScript, [control], ControlSyncArguments(record), Master);

    internal static Task<RedisResult> QueueControlCommit(ITransaction transaction, RedisKey control, long ep, string id) =>
        transaction.ScriptEvaluateAsync(CtlCommitScript, [control], [ep, id], Master);

    internal static RecordOp TransitOp(RedisKey state, StateExpectation expected, StateTarget target, LeaseFence? fence)
    {
        _ = TransitArguments(expected, target, fence);
        return new RecordOp(
            state,
            t => QueueTransit(t, state, expected, target, fence),
            held => PlanTransit(state, held, expected, target, fence),
            held => Field(held, StateFields.Ep) == target.Ep.ToString(CultureInfo.InvariantCulture) && Field(held, StateFields.Id) == target.Id,
            fence);
    }

    internal static RecordOp CommitBeginOp(RedisKey state, string id, LeaseFence? fence) =>
        new(
            state,
            t => QueueCommitBegin(t, state, id, fence),
            held => Field(held, StateFields.Id) != id
                ? new ConditionPlan(ExpectationFailed, [], null)
                : new ConditionPlan(Written, WithFence(fence, Condition.HashEqual(state, StateFields.Id, id)), t => _ = t.HashSetAsync(state, StateFields.CommitBegun, id, When.Always, Master)),
            held => Field(held, StateFields.CommitBegun) == id,
            fence);

    internal static RecordOp DurableOp(RedisKey state, string id, string release) =>
        new(
            state,
            t => QueueDurable(t, state, id, release),
            held => PlanDurable(state, held, id, release),
            held => Field(held, StateFields.Id) == id && Field(held, StateFields.Durable) == "1");

    internal static RecordOp ControlSyncOp(RedisKey control, ControlRecord record) =>
        new(
            control,
            t => QueueControlSync(t, control, record),
            held => PlanControlSync(control, held, record),
            held => Field(held, "ep") == record.Ep.ToString(CultureInfo.InvariantCulture) && Field(held, "id") == record.Id);

    internal static RecordOp ControlCommitOp(RedisKey control, long ep, string id) =>
        new(
            control,
            t => QueueControlCommit(t, control, ep, id),
            held => PlanControlCommit(control, held, ep, id),
            held => Field(held, "ep") == ep.ToString(CultureInfo.InvariantCulture) && Field(held, "id") == id && Field(held, "dur") == "1");

    private static string Field(HashEntry[] held, string name)
    {
        foreach (var entry in held)
        {
            if (entry.Name == name)
            {
                return (string?)entry.Value ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool Has(HashEntry[] held, string name) => held.Any(e => e.Name == name);

    private static Condition[] WithFence(LeaseFence? fence, params Condition[] conditions) =>
        fence is null ? conditions : [Condition.StringEqual(fence.Key, fence.Token), .. conditions];

    private static Condition[] IdentityConditions(RedisKey key, HashEntry[] held, string epField, string idField) =>
    [
        Has(held, epField) ? Condition.HashEqual(key, epField, Field(held, epField)) : Condition.HashNotExists(key, epField),
        Has(held, idField) ? Condition.HashEqual(key, idField, Field(held, idField)) : Condition.HashNotExists(key, idField),
    ];

    private static ConditionPlan PlanTransit(RedisKey state, HashEntry[] held, StateExpectation expected, StateTarget target, LeaseFence? fence)
    {
        var (_, optional, clear) = TransitArguments(expected, target, fence);
        var ep = Field(held, StateFields.Ep);
        var id = Field(held, StateFields.Id);
        if (Has(held, StateFields.Ep) && ep == target.Ep.ToString(CultureInfo.InvariantCulture) && id == target.Id)
        {
            return new ConditionPlan(Repeated, WithFence(fence, IdentityConditions(state, held, StateFields.Ep, StateFields.Id)), t => _ = t.HashSetAsync(state, StateFields.Id, target.Id, When.Always, Master));
        }

        Condition[] conditions;
        if (expected.IsAbsent)
        {
            if (held.Length > 0)
            {
                return new ConditionPlan(ExpectationFailed, [], null);
            }

            conditions = WithFence(fence, Condition.KeyNotExists(state));
        }
        else
        {
            if (ep != expected.Ep?.ToString(CultureInfo.InvariantCulture) || id != expected.Id || Field(held, StateFields.Durable) != expected.Durable.ToString(CultureInfo.InvariantCulture)
                || (expected.Abandon && Field(held, StateFields.CommitBegun) == expected.Id))
            {
                return new ConditionPlan(ExpectationFailed, [], null);
            }

            // A begin that lands after the read must fail the transaction, as the script would refuse it.
            conditions =
            [
                .. WithFence(fence, IdentityConditions(state, held, StateFields.Ep, StateFields.Id)),
                Condition.HashEqual(state, StateFields.Durable, Field(held, StateFields.Durable)),
                Has(held, StateFields.CommitBegun) ? Condition.HashEqual(state, StateFields.CommitBegun, Field(held, StateFields.CommitBegun)) : Condition.HashNotExists(state, StateFields.CommitBegun),
            ];
        }

        return new ConditionPlan(Written, conditions, TransitConditionWrite(state, target, optional, clear));
    }

    private static Action<ITransaction> TransitConditionWrite(RedisKey state, StateTarget target, List<(string Field, RedisValue Value)> optional, IReadOnlyList<string> clear)
    {
        var entries = new List<HashEntry>
        {
            new(StateFields.State, target.State), new(StateFields.Direction, target.Direction), new(StateFields.NPop, target.NPop),
            new(StateFields.Ep, target.Ep), new(StateFields.Id, target.Id), new(StateFields.Durable, "0"), new(StateFields.Prev, target.Prev),
            new(StateFields.Updated, target.UpdatedAt),
        };
        entries.AddRange(optional.Select(o => new HashEntry(o.Field, o.Value)));
        var deletes = clear.Where(f => optional.All(o => o.Field != f)).Select(f => (RedisValue)f).ToArray();
        return transaction =>
        {
            if (deletes.Length > 0)
            {
                _ = transaction.HashDeleteAsync(state, deletes, Master);
            }

            _ = transaction.HashSetAsync(state, [.. entries], Master);
        };
    }

    private static ConditionPlan PlanDurable(RedisKey state, HashEntry[] held, string id, string release)
    {
        string[] fields = [StateFields.Id, StateFields.State, StateFields.Direction, StateFields.NPop, StateFields.Ep];
        if (Field(held, StateFields.Id) != id || fields.Skip(1).Any(f => !Has(held, f)))
        {
            return new ConditionPlan(ExpectationFailed, [], null);
        }

        var entries = new List<HashEntry>
        {
            new(StateFields.Durable, "1"),
            new(StateFields.Prev, $"{Field(held, StateFields.State)}:{Field(held, StateFields.Direction)}:{Field(held, StateFields.NPop)}:{Field(held, StateFields.Ep)}:{id}"),
        };
        if (release.Length > 0)
        {
            entries.Add(new HashEntry(StateFields.Release, release));
        }

        return new ConditionPlan(Written, [.. fields.Select(f => Condition.HashEqual(state, f, Field(held, f)))], t => _ = t.HashSetAsync(state, [.. entries], Master));
    }

    private static ConditionPlan PlanControlSync(RedisKey control, HashEntry[] held, ControlRecord record)
    {
        _ = ControlSyncArguments(record);
        var conditions = IdentityConditions(control, held, "ep", "id");
        if (Has(held, "ep"))
        {
            var current = long.Parse(Field(held, "ep"), CultureInfo.InvariantCulture);
            if (record.Ep < current)
            {
                return new ConditionPlan(AlreadyWrittenOrOlder, [], null);
            }

            if (record.Ep == current)
            {
                return Field(held, "id") != record.Id
                    ? new ConditionPlan(IdentityConflict, [], null)
                    : new ConditionPlan(AlreadyWrittenOrOlder, conditions, t => _ = t.HashSetAsync(control, "id", record.Id, When.Always, Master));
            }
        }

        // A gate added since the read changes the length, so the delete never misses one.
        var gates = held.Where(e => e.Name.ToString().StartsWith("c:", StringComparison.Ordinal)).Select(e => e.Name).ToArray();
        return new ConditionPlan(Written, [.. conditions, Condition.HashLengthEqual(control, held.Length)], t => WriteControlRecord(t, control, record, gates));
    }

    private static void WriteControlRecord(ITransaction transaction, RedisKey control, ControlRecord record, RedisValue[] gates)
    {
        _ = transaction.HashSetAsync(
            control,
            [
                new("ep", record.Ep), new("id", record.Id), new("st", record.State), new("dir", record.Direction), new("np", record.NPop), new("dur", "0"),
                new("pst", record.PreviousState), new("pdir", record.PreviousDirection), new("pnp", record.PreviousNPop), new("pep", record.PreviousEp), new("pid", record.PreviousId),
            ],
            Master);
        if (gates.Length > 0)
        {
            _ = transaction.HashDeleteAsync(control, gates, Master);
        }

        if (record.Gates.Count > 0)
        {
            _ = transaction.HashSetAsync(control, [.. record.Gates.Select(g => new HashEntry("c:" + g.Cid, "OPEN:" + g.Nonce))], Master);
        }
    }

    private static ConditionPlan PlanControlCommit(RedisKey control, HashEntry[] held, long ep, string id)
    {
        if (!Has(held, "ep") || long.Parse(Field(held, "ep"), CultureInfo.InvariantCulture) != ep)
        {
            return new ConditionPlan(AlreadyWrittenOrOlder, [], null);
        }

        return Field(held, "id") != id
            ? new ConditionPlan(IdentityConflict, [], null)
            : new ConditionPlan(Written, IdentityConditions(control, held, "ep", "id"), t => _ = t.HashSetAsync(control, "dur", "1", When.Always, Master));
    }
}
