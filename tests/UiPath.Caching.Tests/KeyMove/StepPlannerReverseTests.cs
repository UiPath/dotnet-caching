using UiPath.Caching.KeyMove;
using static UiPath.Caching.Tests.KeyMove.PlannerKit;

namespace UiPath.Caching.Tests.KeyMove;

// X6 to X10, the reverse gates, and the flag direction rules (reverse_blocked).
public class StepPlannerReverseTests
{
    private static readonly StateRecord Ready = State("S1", ep: 11, prev: "S0:none:0:10:id10");
    private static readonly StateRecord Moving = State("S3", ep: 13, nPop: 1, prev: "S2:fwd:0:12:id12");
    private static readonly StateRecord Drained = State("S4", ep: 14, nPop: 1, prev: "S3:fwd:1:13:id13");
    private static readonly StateRecord ReverseReady = State("S1", "rev", ep: 15, nPop: 1, prev: "S4:fwd:1:14:id14");
    private static readonly StateRecord ReverseMoving = State("S3", "rev", ep: 16, nPop: 1, prev: "S1:rev:1:15:id15");
    private static readonly StateRecord ReverseDrained = State("S4", "rev", ep: 17, nPop: 1, prev: "S3:rev:1:16:id16").With((StateFields.ResidueId, "id17"));

    [Fact]
    public async Task X6_returns_S1_or_S2_to_S0_once_a_lower_view_persisted_and_releases_the_claim_of_a_group_that_holds_one()
    {
        var plan = await DecideAsync(Input(Ready with { Claim = "tok" }, flag: Verdict(min: 3, lowered: 2)));

        Takes(plan, "X6", "S0", "none", 0);
        plan.Spec!.Should().Match<TransitionSpec>(s => s.ReleaseClaim && s.WayBack);

        var freeze = State("S2", ep: 12, prev: "S1:fwd:0:11:id11");
        Takes(await DecideAsync(Input(freeze, flag: Verdict(min: 3, lowered: 0))), "X6", "S0", "none", 0);
        (await DecideAsync(Input(freeze, flag: Verdict(min: 3, lowered: 0)))).Spec!.ReleaseClaim.Should().BeFalse();
    }

    [Fact]
    public async Task A_lower_view_that_has_not_persisted_starts_no_way_back()
    {
        var plan = await DecideAsync(Input(Ready, flag: Verdict(min: 2, lowered: null), fresh: [Advert("n1", fl: 2), Advert("n2", fl: 3)]));

        plan.Kind.Should().Be(StepKind.None);
    }

    [Fact]
    public async Task X7_runs_through_the_reverse_gates_and_names_the_gate_that_fails()
    {
        var lowered = Verdict(min: 3, lowered: 1);

        var plan = await DecideAsync(Input(Moving, flag: lowered));
        Takes(plan, "X7", "S1", "rev", 1);
        plan.Spec!.NewMoveId.Should().BeTrue();

        var blocked = await DecideAsync(Input(Moving, [Advert("n1", acks: Moving), Advert("n2", km: false)], lowered));
        blocked.Kind.Should().Be(StepKind.None);
        blocked.AlertList.Should().Contain(a => a.Code == "reverse_blocked" && a.Detail == "start.km_support:n2");

        (await DecideAsync(Input(Moving, [Advert("n1"), Advert("n2", pin: false)], lowered))).AlertList.Should().Contain(a => a.Code == "reverse_blocked" && a.Detail == "start.pins:n2");
        (await DecideAsync(Input(Moving, [Advert("n1"), Advert("n2", snap: 90)], lowered))).AlertList.Should().Contain(a => a.Code == "reverse_blocked" && a.Detail == "start.snapshot_freshness:n2");
        (await DecideAsync(Input(Moving, facts: new FakeGroupFacts { NodeListSettled = false }, flag: lowered))).AlertList.Should().Contain(a => a.Code == "reverse_blocked" && a.Detail == "start.node_list:node_list_incomplete");
    }

    [Fact]
    public async Task A_stored_forced_reverse_skips_G1_to_G3_and_G7_and_is_counted_but_nothing_else_is_asked_of_the_start_gates()
    {
        var lowered = Verdict(min: 3, lowered: 1);
        var fresh = new[] { Advert("n1"), Advert("n2", km: false, snap: 90) };

        var plan = await DecideAsync(Input(Moving.Approved(ApprovalKinds.ForceReverse), fresh, lowered, facts: new FakeGroupFacts { Settled = false }));

        Takes(plan, "X7", "S1", "rev", 1);
        plan.AlertList.Should().Contain(new StageAlert("gate_override", ApprovalKinds.ForceReverse));
    }

    [Fact]
    public async Task X8_waits_for_every_fresh_advert_to_ack_S1_of_the_reverse_and_X9_needs_D1_to_D3_with_no_canary_or_hold()
    {
        Takes(await DecideAsync(Input(ReverseReady, flag: Verdict(min: 5))), "X8", "S3", "rev", 1);
        var waiting = await DecideAsync(Input(ReverseReady, [Advert("n1", acks: ReverseReady), Advert("n2", acks: Drained)]));
        waiting.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("stage_pending", "ack:n2"));

        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained(canary: 9) };
        var held = ReverseMoving with { HoldOp = "x", HoldAuto = "canary" };
        Takes(await DecideAsync(Input(held, mover: mover)), "X9", "S4", "rev", 1);
        mover.Directions.Should().Contain("rev");

        (await DecideAsync(Input(ReverseMoving, mover: new FakeMoverStatus()))).AlertList.Single().Detail.Should().Be("drained:drain.confirming_pass,drain.skipped_accounted");
        mover.Report = FakeMoverStatus.Drained();
        (await DecideAsync(Input(ReverseMoving, [Advert("n1", acks: ReverseMoving, pi: 1)], mover: mover))).AlertList.Single().Detail.Should().Be("drained:drain.promotions_idle");
    }

    [Fact]
    public async Task X10_needs_the_stored_release_consent_then_sweeps_N_before_the_flip_to_S0()
    {
        var waiting = await DecideAsync(Input(ReverseDrained with { Residue = new Dictionary<string, long> { ["a"] = 0 } }));
        waiting.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("stage_pending", "release_consent"));

        var plan = await DecideAsync(Input((ReverseDrained with { Claim = "tok", Residue = new Dictionary<string, long> { ["a"] = 1 } }).Approved(ApprovalKinds.Release)));

        Takes(plan, "X10", "S0", "none", 0);
        plan.Spec!.Should().Match<TransitionSpec>(s => s.Pre == PreStep.SweepNative && s.ReleaseClaim && s.WayBack);
    }

    [Fact]
    public async Task A_release_consent_is_void_when_the_inventory_it_covers_changes()
    {
        var consented = (ReverseDrained with { Residue = new Dictionary<string, long> { ["a"] = 1 } }).Approved(ApprovalKinds.Release);

        var plan = await DecideAsync(Input(consented with { Residue = new Dictionary<string, long> { ["a"] = 2 } }));

        plan.Kind.Should().Be(StepKind.None);
        plan.AlertList.Should().ContainSingle().Which.Detail.Should().Be("release_consent");
    }

    [Fact]
    public async Task The_way_back_is_never_paused_by_a_hold()
    {
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained() };

        Takes(await DecideAsync(Input(ReverseReady with { HoldOp = "x" })), "X8", "S3", "rev", 1);
        Takes(await DecideAsync(Input(ReverseMoving with { HoldAuto = "no_km:n9" }, mover: mover)), "X9", "S4", "rev", 1);
    }

    [Fact]
    public async Task A_reverse_runs_to_S0_whatever_the_flag_does_and_a_further_lowering_changes_nothing()
    {
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained() };

        // The flag back at 5 while the reverse runs: the library does not turn round.
        Takes(await DecideAsync(Input(ReverseReady, flag: Verdict(min: 5))), "X8", "S3", "rev", 1);
        Takes(await DecideAsync(Input(ReverseMoving, flag: Verdict(min: 5), mover: mover)), "X9", "S4", "rev", 1);

        // A further lowering during the reverse is the same plan, and X6 never runs in reverse.
        var lowered = Verdict(min: 3, lowered: 0);
        Takes(await DecideAsync(Input(ReverseReady, flag: lowered)), "X8", "S3", "rev", 1);
        Takes(await DecideAsync(Input(ReverseMoving, flag: lowered, mover: mover)), "X9", "S4", "rev", 1);
        (await DecideAsync(Input(ReverseDrained, flag: lowered))).Kind.Should().Be(StepKind.None);
    }

    [Fact]
    public async Task A_raise_during_a_reverse_waits_for_S0_and_forward_starts_again_only_when_the_flag_still_asks()
    {
        var consented = (ReverseDrained with { Residue = new Dictionary<string, long> { ["a"] = 0 } }).Approved(ApprovalKinds.Release);
        var raised = Verdict(min: 5);

        Takes(await DecideAsync(Input(consented, flag: raised)), "X10", "S0", "none", 0);

        var back = State("S0", "none", ep: 18, prev: "S4:rev:1:17:id17").With((StateFields.DryRun, new DryRunStamp(DateTimeOffset.UnixEpoch.AddDays(29), 1, "ok").Encode()));
        Takes(await DecideAsync(Input(back.Approved(ApprovalKinds.Start), flag: raised)), "X1", "S1", "fwd", 0);
        (await DecideAsync(Input(back.Approved(ApprovalKinds.Start), flag: Verdict(min: 2)))).Kind.Should().Be(StepKind.None);
    }

    [Fact]
    public async Task The_flag_below_5_in_S5_is_ignored_with_an_alert_and_nothing_goes_back()
    {
        var closed = State("S5", ep: 20, nPop: 1, prev: "S4:fwd:1:19:id19");

        var plan = await DecideAsync(Input(closed, flag: Verdict(min: 4, lowered: 2)));

        plan.Kind.Should().Be(StepKind.None);
        plan.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("flag_below_closed", Group));
        (await DecideAsync(Input(closed, flag: Verdict(min: 5)))).AlertList.Should().BeEmpty();
    }

    [Fact]
    public async Task The_only_way_out_of_S5_is_the_operator_abandon_for_groups_that_accept_a_refill()
    {
        var closed = State("S5", ep: 20, nPop: 1, prev: "S4:fwd:1:19:id19");

        var refused = await DecideAsync(Input(closed.Approved(ApprovalKinds.Abandon), flag: Verdict(min: 5)));
        refused.AlertList.Should().ContainSingle().Which.Code.Should().Be("abandon_refused");

        var plan = await DecideAsync(Input(closed.Approved(ApprovalKinds.Abandon), flag: Verdict(min: 5), facts: new FakeGroupFacts { AcceptRefill = true }));
        plan.Kind.Should().Be(StepKind.ColdStart);

        (await DecideAsync(Input(closed, flag: Verdict(min: 5), facts: new FakeGroupFacts { AcceptRefill = true }))).Kind.Should().Be(StepKind.None, "without the stored abandon nothing leaves S5");
    }

    [Fact]
    public async Task A_cold_start_in_progress_is_continued_from_its_marker()
    {
        var flipped = State("S0", "none", ep: 21, prev: "S5:fwd:1:20:id20").With((StateFields.ColdStart, "tok"));

        (await DecideAsync(Input(flipped))).Kind.Should().Be(StepKind.ColdStart);
    }

    private static Task<StepPlan> DecideAsync(StepInput input) => StepPlanner.DecideAsync(input, Defaults);

    private static void Takes(StepPlan plan, string code, string state, string direction, int nPop) =>
        plan.Should().Match<StepPlan>(p => p.Kind == StepKind.Transit && p.Spec!.Code == code && p.Spec.State == state && p.Spec.Direction == direction && p.Spec.NPop == nPop);
}
