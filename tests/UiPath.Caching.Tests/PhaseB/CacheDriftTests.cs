using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;
using static UiPath.Caching.Tests.PhaseB.PlannerKit;

namespace UiPath.Caching.Tests.PhaseB;

// T-B52: a cache outside the stored set is unmanaged on the node, and the leader brings it in by X12 only in S1 and S2.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class CacheDriftTests(RedisContainerFixture fixture)
{
    private static readonly StateRecord Ready = State("S1", ep: 11, prev: "S0:none:0:10:id10");

    [Theory]
    [InlineData("S1", "S0:none:0:9:id9")]
    [InlineData("S2", "S1:fwd:0:9:id9")]
    public async Task Every_fresh_advert_with_the_same_other_set_rebuilds_in_S1_and_S2(string stage, string prev)
    {
        var state = State(stage, caches: "a", prev: prev);
        var fresh = new[] { Advert("n1", ca: "a,b", acks: state), Advert("n2", ca: "b,a", acks: state) };

        var plan = await StepPlanner.DecideAsync(Input(state, fresh), Defaults);

        plan.Kind.Should().Be(StepKind.RebuildCaches);
        plan.Caches.Should().Be("a+b");
        plan.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("cache_set_drift", "n1,n2"));
    }

    [Fact]
    public async Task Adverts_that_disagree_or_say_nothing_never_rebuild_and_are_reported()
    {
        var disagree = await StepPlanner.DecideAsync(Input(Ready, [Advert("n1", ca: "a,b", acks: Ready), Advert("n2", ca: "a", acks: Ready)]), Defaults);
        var silent = await StepPlanner.DecideAsync(Input(Ready, [Advert("n1", ca: "a,b", acks: Ready), Advert("n2", ca: null, acks: Ready)]), Defaults);

        disagree.Kind.Should().NotBe(StepKind.RebuildCaches);
        disagree.AlertList.Should().Contain(new StageAlert("cache_set_drift", "n1"));
        silent.Kind.Should().NotBe(StepKind.RebuildCaches);
        silent.AlertList.Should().Contain(new StageAlert("cache_set_drift", "n1,n2"));
    }

    [Theory]
    [InlineData("S3", 1, "S2:fwd:0:9:id9")]
    [InlineData("S4", 1, "S3:fwd:1:9:id9")]
    [InlineData("S5", 1, "S4:fwd:1:9:id9")]
    public async Task From_S3_on_the_set_is_frozen_and_a_new_cache_is_only_reported(string stage, int nPop, string prev)
    {
        var state = State(stage, nPop: nPop, caches: "a", prev: prev);
        var fresh = new[] { Advert("n1", ca: "a,b", acks: state), Advert("n2", ca: "a,b", acks: state) };

        var plan = await StepPlanner.DecideAsync(Input(state, fresh), Defaults);

        plan.Kind.Should().NotBe(StepKind.RebuildCaches);
        plan.Caches.Should().BeNull();
        plan.AlertList.Should().Contain(new StageAlert("cache_set_drift", "n1,n2"));
    }

    [Fact]
    public async Task A_reverse_state_is_frozen_too_and_S0_is_left_to_Init()
    {
        var reverse = State("S1", "rev", nPop: 1, prev: "S3:fwd:1:9:id9");
        var plan = await StepPlanner.DecideAsync(Input(reverse, [Advert("n1", ca: "a,b", acks: reverse), Advert("n2", ca: "a,b", acks: reverse)]), Defaults);
        var off = await StepPlanner.DecideAsync(Input(ReadyOff(), [Advert("n1", ca: "a,b"), Advert("n2", ca: "a,b")]), Defaults);

        plan.Kind.Should().NotBe(StepKind.RebuildCaches);
        off.AlertList.Select(a => a.Code).Should().NotContain("cache_set_drift");
    }

    [Fact]
    public async Task A_cache_name_a_state_cannot_hold_and_a_lowered_flag_do_not_rebuild()
    {
        var bad = await StepPlanner.DecideAsync(Input(Ready, [Advert("n1", ca: "a,b@c", acks: Ready), Advert("n2", ca: "a,b@c", acks: Ready)]), Defaults);
        var lowered = await StepPlanner.DecideAsync(Input(Ready, [Advert("n1", ca: "a,b", acks: Ready), Advert("n2", ca: "a,b", acks: Ready)], Verdict(min: 2, lowered: 2)), Defaults);

        bad.Kind.Should().NotBe(StepKind.RebuildCaches);
        lowered.Kind.Should().NotBe(StepKind.RebuildCaches, "the way back to S0 refreshes the set through Init");
    }

    [Fact]
    public async Task The_same_set_raises_nothing()
    {
        var plan = await StepPlanner.DecideAsync(Input(Ready, [Advert("n1", acks: Ready), Advert("n2", acks: Ready)]), Defaults);

        plan.AlertList.Select(a => a.Code).Should().NotContain("cache_set_drift");
    }

    [Fact]
    public async Task The_leader_rebuilds_in_S1_under_a_new_identity_with_the_gates_of_the_new_set_and_X3_waits_for_the_acks_of_it()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        var ready = await kit.StepUntilAsync("S1");
        kit.Fleet.All(n => n.Ca = "a,b");
        kit.Fleet.All(n => n.Acks = false);

        for (var step = 0; step < 6 && (await kit.StateAsync()).Ep == ready.Ep; step++)
        {
            await kit.StepAsync();
        }

        var rebuilt = await kit.StateAsync();
        rebuilt.Ep.Should().Be(ready.Ep + 1);
        rebuilt.Id.Should().NotBe(ready.Id);
        rebuilt.CacheIds.Should().Equal("a", "b");
        rebuilt.State.Should().Be("S1");
        (await kit.Kit.ControlDb.HashGetAllAsync(kit.Kit.Names.Control(kit.Group, 0))).Select(e => (string)e.Name!).Should().Contain(["c:a", "c:b"]);

        for (var step = 0; step < 4; step++)
        {
            await kit.StepAsync();
        }

        (await kit.StateAsync()).State.Should().Be("S1", "no node acknowledged the new identity yet");
        kit.Fleet.All(n => n.Acks = true);
        (await kit.StepUntilAsync("S3")).CacheIds.Should().Equal("a", "b");
    }

    [Fact]
    public async Task A_cache_registered_after_X3_leaves_the_set_alone_and_is_reported()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        var moving = await kit.StepUntilAsync("S3");
        kit.Fleet.All(n => n.Ca = "a,b");

        for (var step = 0; step < 4; step++)
        {
            await kit.StepAsync();
        }

        var after = await kit.StateAsync();
        after.Ep.Should().Be(moving.Ep);
        after.CacheIds.Should().Equal("a");
        kit.Alerts.Should().Contain(a => a.Code == "cache_set_drift");
    }

    [Fact]
    public async Task A_listed_node_manages_only_the_caches_of_the_stored_set_and_reports_the_drift()
    {
        var reader = new FakeStateReader { State = FakeStateReader.Record("S3", 5, nPop: 1) };
        var alerts = new List<(string Group, string Alert)>();
        var monitor = new GroupStateMonitor(reader, new PhaseBStateOptions(), new ManualTimeProvider(DateTimeOffset.UnixEpoch), (g, a) => alerts.Add((g, a)));
        monitor.Register([new GroupRegistration("g1", false, true, ["a", "late"])]);

        await monitor.StepAsync("g1", CancellationToken.None);

        var rules = monitor.RulesFor("g1");
        rules.IsManaged("a").Should().BeTrue();
        rules.IsManaged("late").Should().BeFalse("registered after the set was frozen, it keeps the Phase A path");
        rules.Alerts.Should().Contain("cache_set_drift");
        alerts.Should().Contain(("g1", "cache_set_drift"));
    }

    [Fact]
    public async Task Equal_sets_raise_nothing_and_a_node_that_names_no_caches_is_not_judged()
    {
        var reader = new FakeStateReader { State = FakeStateReader.Record("S1", 5) };
        var equal = new GroupStateMonitor(reader, new PhaseBStateOptions(), new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        equal.Register([new GroupRegistration("g1", false, true, ["a"])]);
        var silent = new GroupStateMonitor(reader, new PhaseBStateOptions(), new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        silent.Register([new GroupRegistration("g1", false, true)]);
        await equal.StepAsync("g1", CancellationToken.None);
        await silent.StepAsync("g1", CancellationToken.None);

        equal.RulesFor("g1").Alerts.Should().BeEmpty();
        silent.RulesFor("g1").Alerts.Should().BeEmpty();
        GroupRules.Unaffected.IsManaged("anything").Should().BeTrue();
    }
}
