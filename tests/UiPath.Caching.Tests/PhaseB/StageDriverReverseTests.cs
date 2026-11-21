using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

// The way back through the leader's loop: FlagLowerHold, X7 gates, X8 to X10, X13, holds, and a raise during a reverse (T-B21, T-B51).
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class StageDriverReverseTests(RedisContainerFixture fixture)
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_jump_down_from_4_to_2_waits_FlagLowerHold_then_runs_X7_X8_X9_and_X10_after_the_release_consent()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var drained = await ToDrainedAsync(kit);
        kit.Fleet.All(n => n.Fl = 2);

        await StepsAsync(kit, 6, Ten);
        (await kit.StateAsync()).State.Should().Be("S4", "a lower view that lasts less than FlagLowerHold starts no X7");

        var reverse = await kit.StepUntilAsync("S1", "rev", advance: Ten);
        reverse.Should().Match<StateRecord>(s => s.NPop == 1 && s.MoveId == s.Ep && s.Ep > drained.Ep);
        (await kit.StepUntilAsync("S3", "rev")).NPop.Should().Be(1);
        await kit.StepUntilAsync("S4", "rev");

        await StepsAsync(kit, 4, Ten);
        (await kit.StateAsync()).State.Should().Be("S4", "X10 needs the stored release consent");
        kit.Alerts.Should().Contain(("stage_pending", "release_consent"));
        (await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Release, "N residue acknowledged", "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);

        var off = await kit.StepUntilAsync("S0", "none");
        off.NPop.Should().Be(0);
        kit.Data.Calls.Should().Contain("purge_n");
        kit.Mover.Directions.Should().Contain("rev");
    }

    [Fact]
    public async Task X7_fails_closed_with_reverse_blocked_while_a_gate_fails_and_a_stored_forced_reverse_goes_ahead_and_is_counted()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await ToDrainedAsync(kit);
        kit.Fleet.All(n => n.Fl = 2);
        kit.Fleet.Nodes[1].Pb = false;

        await StepsAsync(kit, 10, Ten);

        (await kit.StateAsync()).State.Should().Be("S4");
        kit.Alerts.Should().Contain(("reverse_blocked", "G1:n2"));
        (await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.ForceReverse, "n2 is gone", "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);

        await kit.StepUntilAsync("S1", "rev", advance: Ten);
        kit.Alerts.Should().Contain(("gate_override", ApprovalKinds.ForceReverse));
    }

    [Fact]
    public async Task A_hold_never_pauses_the_way_back()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await ToDrainedAsync(kit);
        (await kit.Commands.HoldAsync(kit.Group, "operator pause")).Should().BeTrue();
        kit.Fleet.All(n => n.Fl = 2);

        await kit.StepUntilAsync("S1", "rev", advance: Ten);
        await kit.StepUntilAsync("S3", "rev");
        await kit.StepUntilAsync("S4", "rev");
        (await kit.StateAsync()).HoldOp.Should().Be("operator pause");
    }

    [Fact]
    public async Task X13_is_one_step_back_from_S4_to_S3_after_FlagLowerHold_and_writes_a_new_move_id()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        var drained = await ToDrainedAsync(kit);
        kit.Fleet.All(n => n.Fl = 3);

        await StepsAsync(kit, 5, Ten);
        (await kit.StateAsync()).State.Should().Be("S4");

        var back = await kit.StepUntilAsync("S3", advance: Ten);

        back.MoveId.Should().Be(back.Ep);
        back.Ep.Should().BeGreaterThan(drained.Ep);
    }

    [Fact]
    public async Task A_raise_during_a_reverse_waits_for_S0_and_the_library_then_starts_forward_again_because_the_flag_still_asks()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await ToDrainedAsync(kit);
        kit.Fleet.All(n => n.Fl = 2);
        await kit.StepUntilAsync("S1", "rev", advance: Ten);

        kit.Fleet.All(n => n.Fl = 5);
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        await kit.StepUntilAsync("S3", "rev");
        await kit.StepUntilAsync("S4", "rev");
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Release, "ok", "op", CancellationToken.None);
        var off = await kit.StepUntilAsync("S0", "none");

        var again = await kit.StepUntilAsync("S1");

        again.Ep.Should().BeGreaterThan(off.Ep);
        again.MoveId.Should().Be(again.Ep);
    }

    [Fact]
    public async Task A_further_lowering_during_a_reverse_changes_nothing_and_the_reverse_never_runs_X6()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await ToDrainedAsync(kit);
        kit.Fleet.All(n => n.Fl = 2);
        await kit.StepUntilAsync("S1", "rev", advance: Ten);

        kit.Fleet.All(n => n.Fl = 0);
        var trail = new List<string>();
        for (var n = 0; n < 12; n++)
        {
            var outcome = await kit.StepAsync(Ten);
            trail.Add(outcome?.Plan.Spec?.Code ?? "-");
            if ((await kit.StateAsync()).State == "S4")
            {
                break;
            }
        }

        trail.Should().NotContain("X6");
        (await kit.StateAsync()).Direction.Should().Be("rev");
    }

    [Fact]
    public async Task The_flag_below_5_in_S5_is_ignored_with_an_alert_and_nothing_goes_back()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await ToDrainedAsync(kit);
        await kit.Commands.ApproveAsync(kit.Group, ApprovalKinds.Close, "residue acknowledged", "op", CancellationToken.None);
        var closed = await kit.StepUntilAsync("S5", advance: TimeSpan.FromSeconds(30));
        kit.Fleet.All(n => n.Fl = 1);

        await StepsAsync(kit, 12, Ten);

        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.State == "S5" && s.Ep == closed.Ep);
        kit.Alerts.Should().Contain(("flag_below_closed", kit.Group));
    }

    [Fact]
    public async Task Resume_clears_the_operator_hold_and_forward_continues()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S1");
        await kit.Commands.HoldAsync(kit.Group, "pause");

        await StepsAsync(kit, 5, TimeSpan.FromSeconds(1));
        (await kit.StateAsync()).State.Should().Be("S1");
        kit.Alerts.Should().Contain(("held", "op:pause"));

        (await kit.Commands.ResumeAsync(kit.Group)).Should().BeTrue();
        await kit.StepUntilAsync("S3");
    }

    [Fact]
    public async Task A_node_without_pb_sets_the_automatic_hold_and_the_leader_clears_it_when_the_cause_is_gone()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S1");
        kit.Fleet.Nodes[1].Pb = false;

        await StepsAsync(kit, 4, TimeSpan.FromSeconds(1));
        var held = await kit.StateAsync();
        held.State.Should().Be("S1");
        held.HoldAuto.Should().Be("no_pb:n2");

        kit.Fleet.Nodes[1].Pb = true;
        await kit.StepUntilAsync("S3");
        (await kit.StateAsync()).HoldAuto.Should().BeEmpty();
    }

    [Fact]
    public async Task A_canary_over_its_threshold_holds_X4_and_a_clean_pass_clears_only_the_automatic_hold()
    {
        await using var kit = await DriverKit.CreateAsync(fixture);
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S3");
        kit.Mover.Report = FakeMoverStatus.Drained(canary: 6, ("a", 0));
        await kit.Commands.HoldAsync(kit.Group, "operator");

        await StepsAsync(kit, 4, TimeSpan.FromSeconds(1));
        var held = await kit.StateAsync();
        held.Should().Match<StateRecord>(s => s.State == "S3" && s.HoldAuto == "canary" && s.HoldOp == "operator");

        kit.Mover.Report = FakeMoverStatus.Drained(canary: 0, ("a", 0));
        await StepsAsync(kit, 3, TimeSpan.FromSeconds(1));
        (await kit.StateAsync()).Should().Match<StateRecord>(s => s.HoldAuto == string.Empty && s.HoldOp == "operator" && s.State == "S3");

        await kit.Commands.ResumeAsync(kit.Group);
        await kit.StepUntilAsync("S4");
    }

    private static async Task<StateRecord> ToDrainedAsync(DriverKit kit)
    {
        await kit.PrepareAsync();
        await kit.StepUntilAsync("S3");
        kit.Mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        kit.Mover.Census = new Dictionary<string, long> { ["a"] = 0 };
        return await kit.StepUntilAsync("S4");
    }

    private static async Task StepsAsync(DriverKit kit, int count, TimeSpan advance)
    {
        for (var n = 0; n < count; n++)
        {
            await kit.StepAsync(advance);
        }
    }
}
