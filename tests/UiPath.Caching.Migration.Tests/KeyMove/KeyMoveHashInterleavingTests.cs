using UiPath.Caching.Migration.Tests.KeyMove.Harness;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Every Redis command of a hash promotion and of the writers racing it is a schedule point, and a scheduler picks which actor takes the next step.
// The oracle keeps what each operation answered. N may only ever show a hash that some ordered subset of the operations makes, whole and with its values, and the hash at the end must be what the operations that were acknowledged make in some order: an acknowledged merge is in it unless an operation after it replaced or deleted what it wrote.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashInterleavingTests(RedisFixture fixture)
{
    private static readonly Dictionary<string, string> Original = new() { ["a"] = "1", ["b"] = "2", ["c"] = "3" };

    private static CancellationToken Ct => CancellationToken.None;

    private static Scenario[] Scenarios { get; } =
    [
        new("a promotion against a merge of a new field", k => [Read(k), Merge(k, "d")]),
        new("a promotion against a merge over an existing field", k => [Read(k), Merge(k, "a")]),
        new("a promotion against a replace", k => [Read(k), Replace(k, "x")]),
        new("a promotion against a field delete", k => [Read(k), DeleteField(k, "a")]),
        new("a promotion against a delete", k => [Read(k), Delete(k)]),
        new("a merge against a field delete", k => [Merge(k, "d"), DeleteField(k, "a")]),
        new("a merge over a field against a field delete of it", k => [Merge(k, "a"), DeleteField(k, "a")]),
        new("a merge against a replace", k => [Merge(k, "d"), Replace(k, "x")]),
        new("two promotions against a merge", k => [Read(k), Read(k), Merge(k, "d")], 120),
    ];

    public static TheoryData<int, bool> Cases()
    {
        var data = new TheoryData<int, bool>();
        for (var i = 0; i < Scenarios.Length; i++)
        {
            data.Add(i, true);
        }

        foreach (var i in new[] { 0, 1, 2, 5 })
        {
            data.Add(i, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task No_schedule_shows_a_hash_no_sequence_of_the_operations_makes_and_every_acknowledged_effect_is_in_the_end(int scenarioIndex, bool scripts)
    {
        var scenario = Scenarios[scenarioIndex];
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        var scheduler = new StepScheduler();
        var seen = new List<Dictionary<string, string>>();
        kit.Kit.Schedule.Hook = async (_, _) =>
        {
            var fields = await kit.FieldsAsync(kit.N("k"));
            if (fields.Count > 0)
            {
                lock (seen)
                {
                    seen.Add(fields);
                }
            }

            await scheduler.ParkAsync();
        };
        await kit.Kit.PrepareAsync("k");

        var (runs, violations) = await Interleaver.ExploreAsync(
            async choose =>
            {
                await ResetAsync(kit);
                lock (seen)
                {
                    seen.Clear();
                }

                var outcomes = new WriteOutcome?[scenario.Operations.Length];
                var actors = scenario.Operations.Select((op, i) => (Func<Task>)(async () => outcomes[i] = await op.Run(kit))).ToArray();
                var trace = await scheduler.RunAsync(actors, choose);
                var found = Judge(scenario, seen);

                var final = await kit.Client.ReadAsync("k", null, Ct);
                var state = final.Kind == ReadOutcome.Found ? final.Fields!.ToDictionary(f => f.Key, f => f.Value!.Name) : null;
                if (final.Kind is not (ReadOutcome.Found or ReadOutcome.Absent))
                {
                    found.Add($"the final read answered {final.Kind}");
                }
                else if (!Acknowledged(scenario, outcomes).Any(order => Same(Apply(order, Original), state)))
                {
                    found.Add($"the hash at the end is {Describe(state)}, which no order of the acknowledged operations ({string.Join(", ", outcomes.Select((o, i) => $"{scenario.Operations[i].Name}={o}"))}) makes");
                }

                // A compare that lost to a field delete leaves L as a copy that N outranks; it is never what is read.
                if (await kit.Kit.ExistsAsync(kit.L("k")) && !await kit.Kit.ExistsAsync(kit.N("k")))
                {
                    found.Add("L holds the key and N does not");
                }

                return new InterleavingRun(trace, found);
            },
            scenario.MaxRuns,
            scenario.Operations.Length > 2);

        runs.Should().BeGreaterThan(1, scenario.Name);
        violations.Should().BeEmpty(scenario.Name);
    }

    // The oracle itself: a merge that answered committed and is not in the hash fails it, and a replace after it is the one thing that excuses its absence.
    [Fact]
    public void The_oracle_refuses_a_lost_acknowledged_merge_and_a_changed_value_and_excuses_only_a_later_conflicting_operation()
    {
        var merge = new Scenario("m", k => [Merge(k, "a")]);
        var both = new Scenario("mr", k => [Merge(k, "d"), Replace(k, "x")]);
        var withMerge = new Dictionary<string, string>(Original) { ["d"] = "m" };

        Acknowledged(merge, [WriteOutcome.Committed]).Any(o => Same(Apply(o, Original), Original)).Should().BeFalse("the merge changed a, and the original still has 1");
        Acknowledged(merge, [WriteOutcome.Committed]).Any(o => Same(Apply(o, Original), new Dictionary<string, string>(Original) { ["a"] = "m" })).Should().BeTrue();
        Acknowledged(merge, [WriteOutcome.NotSent]).Any(o => Same(Apply(o, Original), Original)).Should().BeTrue("a merge that was not sent changes nothing");
        Acknowledged(both, [WriteOutcome.Committed, WriteOutcome.Committed]).Any(o => Same(Apply(o, Original), new Dictionary<string, string> { ["x"] = "r" })).Should().BeTrue("the replace after the merge supersedes it");
        Acknowledged(both, [WriteOutcome.Committed, WriteOutcome.Committed]).Any(o => Same(Apply(o, Original), new Dictionary<string, string> { ["d"] = "m" })).Should().BeFalse();
        Acknowledged(both, [WriteOutcome.Committed, WriteOutcome.Committed]).Any(o => Same(Apply(o, Original), withMerge)).Should().BeFalse("a replace acknowledged after the merge leaves no trace of the original");
    }

    private static Operation Read(string key) => new(
        "read",
        async kit =>
        {
            var read = await kit.Client.ReadAsync(key, null, Ct);
            read.Kind.Should().BeOneOf(ReadOutcome.Found, ReadOutcome.Absent);
            return WriteOutcome.Committed;
        },
        state => state,
        false);

    // A merge that meets a promotion that has not finished repeats its own, and after three tries it fails closed: that it is not written is an outcome, never a partial hash.
    private static Operation Merge(string key, string field) => new(
        $"merge {field}",
        async kit => (await kit.Client.SetAsync(key, KeyMoveHashKit.Items((field, "m")), new HashWriteOptions(HashCacheSetOption.HashReplace, null), Ct)).Outcome,
        state => new Dictionary<string, string>(state ?? []) { [field] = "m" },
        true);

    private static Operation Replace(string key, string field) => new(
        $"replace {field}",
        async kit => (await kit.Client.SetAsync(key, KeyMoveHashKit.Items((field, "r")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null), Ct)).Outcome,
        _ => new Dictionary<string, string> { [field] = "r" },
        true);

    private static Operation DeleteField(string key, string field) => new(
        $"delete field {field}",
        async kit => (await kit.Client.RemoveFieldsAsync(key, [field], Ct)).Outcome,
        state =>
        {
            var left = new Dictionary<string, string>(state ?? []);
            left.Remove(field);
            return left.Count == 0 ? null : left;
        },
        true);

    private static Operation Delete(string key) => new(
        "delete",
        async kit => (await kit.Client.RemoveAsync(key, Ct)).Outcome,
        _ => null,
        true);

    private static List<string> Judge(Scenario scenario, List<Dictionary<string, string>> seen)
    {
        var reachable = Reachable(scenario);
        lock (seen)
        {
            return [.. seen.Where(s => !reachable.Any(r => Same(r, s))).Select(s => $"N was seen as {Describe(s)}")];
        }
    }

    // Every state some ordered subset of the operations makes, the original and an absent hash included. A replace is two steps (the delete of L, then the write of N), so an operation that runs between them meets no hash, and its own start is the absent one too.
    private static List<Dictionary<string, string>?> Reachable(Scenario scenario)
    {
        var states = new List<Dictionary<string, string>?>();
        foreach (var order in Orders(Enumerable.Range(0, scenario.Operations.Length).ToList(), subsets: true))
        {
            states.Add(Apply(order.Select(i => scenario.Operations[i]), Original));
            states.Add(Apply(order.Select(i => scenario.Operations[i]), null));
        }

        return states;
    }

    // The operations that answered committed in every order, and the ones that were never sent not at all; a read changes nothing.
    private static IEnumerable<IEnumerable<Operation>> Acknowledged(Scenario scenario, WriteOutcome?[] outcomes)
    {
        var applied = Enumerable.Range(0, scenario.Operations.Length).Where(i => outcomes[i] == WriteOutcome.Committed && scenario.Operations[i].Changes).ToList();
        return Orders(applied, subsets: false).Select(order => order.Select(i => scenario.Operations[i]));
    }

    private static IEnumerable<List<int>> Orders(List<int> items, bool subsets)
    {
        if (items.Count == 0)
        {
            yield return [];
            yield break;
        }

        if (subsets)
        {
            yield return [];
        }

        foreach (var item in items)
        {
            var rest = items.Where(i => i != item).ToList();
            foreach (var tail in Orders(rest, subsets))
            {
                yield return [item, .. tail];
            }
        }
    }

    private static Dictionary<string, string>? Apply(IEnumerable<Operation> order, Dictionary<string, string>? start) =>
        order.Aggregate(start, (state, op) => op.Transform(state));

    private static bool Same(Dictionary<string, string>? left, Dictionary<string, string>? right) =>
        left is null ? right is null : right is not null && left.Count == right.Count && left.All(e => right.TryGetValue(e.Key, out var value) && value == e.Value);

    private static string Describe(Dictionary<string, string>? state) =>
        state is null ? "no hash" : string.Join(',', state.OrderBy(e => e.Key).Select(e => $"{e.Key}={e.Value}"));

    private static async Task ResetAsync(KeyMoveHashKit kit)
    {
        foreach (var key in new[] { kit.L("k"), kit.N("k"), kit.Kit.Names.ForwardFence(kit.N("k")).ToString() })
        {
            await kit.Db.KeyDeleteAsync(key);
        }

        await kit.SetAsync(kit.L("k"), Original);
        await kit.Kit.SetStateAsync("S3");
        kit.Client.Gap();
    }

    private sealed record Operation(string Name, Func<KeyMoveHashKit, Task<WriteOutcome>> Run, Func<Dictionary<string, string>?, Dictionary<string, string>?> Transform, bool Changes);

    private sealed record Scenario(string Name, Func<string, Operation[]> Make, int MaxRuns = 200)
    {
        public Operation[] Operations { get; } = Make("k");
    }
}
