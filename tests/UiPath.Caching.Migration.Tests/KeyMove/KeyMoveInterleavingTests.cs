using System.Collections.Immutable;
using UiPath.Caching.Migration.Tests.KeyMove.Harness;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Runs the harness scenarios with the real client and servers. At each Redis command the scheduler chooses which actor proceeds.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveInterleavingTests(RedisFixture fixture)
{
    private static readonly InterleavingScenario[] Scenarios =
    [
        new("promotion against an origin write", "S3", false, "v0", null, c => [c.Read(), c.Write("v1")]),
        new("promotion against a delete", "S3", false, "v0", null, c => [c.Read(), c.Delete()]),
        new("promotion against a NoOrigin write", "S3", true, "v0", null, c => [c.Read(), c.Write("v1")]),
        new("promotion against a fill", "S3", false, "v0", null, c => [c.Read(), c.Fill("fv")]),
        new("promotion against a fill on a missing key", "S3", false, null, null, c => [c.Read(), c.Fill("fv")]),
        new("origin write against origin write", "S3", false, "v0", null, c => [c.Write("v1"), c.Write("v2")]),
        new("NoOrigin write against NoOrigin write", "S3", true, "v0", "v00", c => [c.Write("v1"), c.Write("v2")]),
        new("write against delete", "S3", false, "v0", "v00", c => [c.Write("v1"), c.Delete()]),
        new("promotion against a NoOrigin delete", "S3", true, "v0", null, c => [c.Read(), c.Delete()]),
        new("NoOrigin write against NoOrigin delete", "S3", true, "v0", "v00", c => [c.Write("v1"), c.Delete()]),
        new("a NoOrigin delete against a reader", "S3", true, "v0", "v00", c => [c.Read(), c.Delete()]),
        new("promotion against a write on S4", "S4", false, "v0", null, c => [c.Read(), c.Write("v1")]),
        new("S5 write against read with residue", "S5", false, "v0", "v00", c => [c.Read(), c.Write("v1")]),
        new("S1 write against read", "S1", false, "v0", null, c => [c.Read(), c.Write("v1")]),
        new("two promoters against a write", "S3", false, "v0", null, c => [c.Read(), c.Read(), c.Write("v1")], 150, true),
    ];

    public static TheoryData<int, string, bool> Cases()
    {
        var data = new TheoryData<int, string, bool>();
        for (var i = 0; i < Scenarios.Length; i++)
        {
            data.Add(i, Placement.One, true);
        }

        // The fallback on one server, the scenarios that cross slots on a cluster, and L and N on two servers.
        foreach (var i in new[] { 0, 1, 2, 3, 5 })
        {
            data.Add(i, Placement.One, false);
        }

        foreach (var i in new[] { 0, 1, 2, 6 })
        {
            data.Add(i, Placement.Cluster, true);
        }

        foreach (var i in new[] { 0, 1, 2, 3, 6, 7, 8, 11 })
        {
            data.Add(i, Placement.Two, true);
        }

        foreach (var i in new[] { 0, 1, 5 })
        {
            data.Add(i, Placement.Two, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task No_schedule_of_the_scenario_breaks_the_invariants_of_the_history(int scenarioIndex, string placement, bool scripts)
    {
        var scenario = Scenarios[scenarioIndex];
        await using var sides = await Placement.OpenAsync(fixture, placement, scripts, scenario.NoOrigin);
        var kit = sides.Kit;
        var scheduler = new StepScheduler();
        kit.Schedule.Hook = (_, _) => scheduler.ParkAsync();
        await kit.PrepareAsync("k");

        var (runs, violations) = await Interleaver.ExploreAsync(choose => RunAsync(sides, scenario, scheduler, choose), scenario.MaxRuns, scenario.Random);

        runs.Should().BeGreaterThan(1, scenario.Name);
        violations.Should().BeEmpty(scenario.Name);
    }

    [Fact]
    public async Task Two_creators_of_a_NoOrigin_claim_have_exactly_one_winner_in_every_schedule()
    {
        await using var sides = await Placement.OpenAsync(fixture, Placement.One, true, noOrigin: true);
        var kit = sides.Kit;
        var scheduler = new StepScheduler();
        kit.Schedule.Hook = (_, _) => scheduler.ParkAsync();
        await kit.PrepareAsync("k");
        var winners = new List<int>();
        var scenario = new InterleavingScenario("two creators", "S3", true, null, null, c => [c.TryAdd("claim-a"), c.TryAdd("claim-b")]);

        var (runs, violations) = await Interleaver.ExploreAsync(
            async choose =>
            {
                var result = await RunAsync(sides, scenario, scheduler, choose, context => winners.Add(context.Created));
                return result;
            },
            scenario.MaxRuns,
            false);

        runs.Should().BeGreaterThan(1);
        violations.Should().BeEmpty();
        winners.Should().OnlyContain(w => w == 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_source_write_requires_the_current_control_record_on_the_source_server(bool scripts)
    {
        await using var sides = await Placement.OpenAsync(fixture, Placement.Two, scripts, noOrigin: false);
        var kit = sides.Kit;
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", nPop: 0);
        await sides.SyncAsync("k");

        (await kit.Client.SetAsync("k", new Item("v1"), null, CancellationToken.None)).Outcome.Should().Be(WriteOutcome.Committed);
        ((string?)await sides.LDb.StringGetAsync(kit.L("k"))).Should().Be("v1");

        await kit.SetStateAsync("S1", nPop: 0);
        (await kit.Client.SetAsync("k", new Item("v2"), null, CancellationToken.None)).Outcome.Should().Be(WriteOutcome.NotSent);
        ((string?)await sides.LDb.StringGetAsync(kit.L("k"))).Should().Be("v1");

        await sides.SyncAsync("k");
        (await kit.Client.SetAsync("k", new Item("v2"), null, CancellationToken.None)).Outcome.Should().Be(WriteOutcome.Committed);
        ((string?)await sides.LDb.StringGetAsync(kit.L("k"))).Should().Be("v2");
        (await kit.Db.KeyExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public void The_ported_oracle_flags_a_read_of_a_value_a_completed_write_replaced_and_a_miss_of_a_stored_key()
    {
        var oracle = new HistoryOracle([new OracleKey("k", false, "v0")]);
        var write = oracle.BeginMutation(0, "v1");
        oracle.EndMutation(0, write);

        var stale = oracle.EndRead(0, oracle.BeginRead(0), "v0", "fwd", false);
        var miss = oracle.EndRead(0, oracle.BeginRead(0), null, "fwd", false);

        stale!.Kind.Should().Be(OracleViolations.Stale);
        miss!.Kind.Should().Be(OracleViolations.FalseMiss);
    }

    private static async Task<InterleavingRun> RunAsync(Placement sides, InterleavingScenario scenario, StepScheduler scheduler, Func<int, int, int> choose, Action<InterleavingContext>? after = null)
    {
        var kit = sides.Kit;
        await ResetAsync(sides, scenario);

        var oracle = new HistoryOracle(ImmutableArray.Create(new OracleKey("k", scenario.NoOrigin, scenario.InitialL, scenario.InitialN)));
        var violations = new List<string>();
        var context = new InterleavingContext(kit, oracle, violations);
        var trace = await scheduler.RunAsync(scenario.Actors(context), choose);

        // After every actor ended, a read that starts later must agree with the history.
        var id = oracle.BeginRead(0);
        var final = await kit.Client.ReadAsync("k", CancellationToken.None);
        if (final.Kind is not (ReadOutcome.Found or ReadOutcome.Absent))
        {
            violations.Add($"the final read answered {final.Kind}");
        }

        if (final.Kind is ReadOutcome.Found or ReadOutcome.Absent && oracle.EndRead(0, id, final.Kind == ReadOutcome.Found ? final.Value!.Name : null, "fwd", true) is { } violation)
        {
            violations.Add(violation.Message);
        }

        after?.Invoke(context);
        return new InterleavingRun(trace, violations);
    }

    private static async Task ResetAsync(Placement sides, InterleavingScenario scenario)
    {
        var kit = sides.Kit;
        await sides.LDb.KeyDeleteAsync(kit.L("k"));
        foreach (var key in new[] { kit.N("k"), kit.Names.ForwardFence(kit.N("k")).ToString() })
        {
            await kit.Db.KeyDeleteAsync(key);
        }

        if (scenario.InitialL is { } l)
        {
            await sides.LDb.StringSetAsync(kit.L("k"), l);
        }

        if (scenario.InitialN is { } n)
        {
            await kit.Db.StringSetAsync(kit.N("k"), n);
        }

        await kit.SetStateAsync(scenario.State);
        await sides.SyncAsync("k");
        kit.Client.Gap();
    }
}
