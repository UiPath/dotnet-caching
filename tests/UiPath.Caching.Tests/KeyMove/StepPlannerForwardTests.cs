using System.Globalization;
using UiPath.Caching.KeyMove;
using static UiPath.Caching.Tests.KeyMove.PlannerKit;

namespace UiPath.Caching.Tests.KeyMove;

// X2 to X5 and X13: the acknowledgement rules, the S2 barrier for NoOrigin caches and the X3 branch rules.
public class StepPlannerForwardTests
{
    private static readonly StateRecord Ready = State("S1", ep: 11, prev: "S0:none:0:10:id10");
    private static readonly StateRecord Freeze = State("S2", ep: 12, prev: "S1:fwd:0:11:id11");
    private static readonly StateRecord Moving = State("S3", ep: 13, nPop: 1, prev: "S2:fwd:0:12:id12");
    private static readonly StateRecord Drained = State("S4", ep: 14, nPop: 1, prev: "S3:fwd:1:13:id13");

    [Fact]
    public async Task X2_runs_only_for_a_group_with_a_NoOrigin_cache_once_every_fresh_advert_acks_S1()
    {
        var facts = new FakeGroupFacts { NoOrigin = true };

        Takes(await DecideAsync(Input(Ready, facts: facts)), "X2", "S2", "fwd", 0);

        var plan = await DecideAsync(Input(Ready, [Advert("n1", acks: Ready), Advert("n2", acks: Freeze)], facts: facts));
        plan.Kind.Should().Be(StepKind.None);
        plan.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("stage_pending", "ack:n2"));
    }

    [Fact]
    public async Task A_NoOrigin_group_never_takes_the_S1_to_S3_branch_and_has_no_stable_view_wait_at_X2()
    {
        var facts = new FakeGroupFacts { NoOrigin = true };

        var plan = await DecideAsync(Input(Ready, facts: facts, stable: TimeSpan.Zero));

        Takes(plan, "X2", "S2", "fwd", 0);
    }

    [Fact]
    public async Task The_S1_branch_of_X3_for_a_group_without_a_NoOrigin_cache_needs_the_view_unchanged_for_AckStable_then_runs_the_inventory_and_sets_nPop()
    {
        var early = await DecideAsync(Input(Ready, stable: TimeSpan.FromSeconds(119)));
        early.Kind.Should().Be(StepKind.None);
        early.AlertList.Should().ContainSingle().Which.Code.Should().Be("stage_pending");

        var plan = await DecideAsync(Input(Ready, stable: TimeSpan.FromMinutes(2)));

        Takes(plan, "X3", "S3", "fwd", 1);
        plan.Spec!.Pre.Should().Be(PreStep.NativeInventory);
        plan.Spec.NewMoveId.Should().BeFalse("only X1, X7 and X13 write moveId");
    }

    [Fact]
    public async Task X3_from_S2_has_no_stable_view_wait_so_the_barrier_lasts_one_refresh_and_the_drain()
    {
        var plan = await DecideAsync(Input(Freeze, stable: TimeSpan.Zero, facts: new FakeGroupFacts { NoOrigin = true }));

        Takes(plan, "X3", "S3", "fwd", 1);
        plan.Spec!.Pre.Should().Be(PreStep.NativeInventory);
    }

    [Fact]
    public async Task X3_waits_for_every_fresh_advert_to_ack_the_current_identity_and_names_the_ones_that_have_not()
    {
        var plan = await DecideAsync(Input(Freeze, [Advert("n1", acks: Freeze), Advert("n2", acks: Ready), Advert("n3", acks: null)]));

        plan.Kind.Should().Be(StepKind.None);
        plan.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("stage_pending", "ack:n2,n3"));
    }

    [Fact]
    public async Task A_forward_step_waits_while_a_replica_is_down_before_it_starts()
    {
        var down = new PreflightResult([new PreflightFailure("replica_unhealthy", "m1"), new PreflightFailure("eviction_policy", "m1")]);

        var plan = await DecideAsync(Input(Freeze, preflight: down));

        plan.Kind.Should().Be(StepKind.None);
        plan.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("replica_unhealthy", "replica_unhealthy"));
    }

    [Fact]
    public async Task X4_needs_the_flag_at_4_and_the_drained_criterion_and_reports_what_is_missing()
    {
        var mover = new FakeMoverStatus();
        var atThree = await DecideAsync(Input(Moving, flag: Verdict(min: 3), mover: mover));
        atThree.Should().Match<StepPlan>(p => p.Kind == StepKind.None && p.AlertList.Count == 0);

        var undrained = await DecideAsync(Input(Moving, flag: Verdict(min: 4), mover: mover));
        undrained.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("stage_pending", "drained:drain.confirming_pass,drain.skipped_accounted"));

        mover.Report = FakeMoverStatus.Drained();
        var promoting = await DecideAsync(Input(Moving, [Advert("n1", acks: Moving, pi: 2), Advert("n2", acks: Moving)], Verdict(min: 4), mover: mover));
        promoting.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("stage_pending", "drained:drain.promotions_idle"));

        mover.Report = FakeMoverStatus.Drained(canary: 1);
        var canary = await DecideAsync(Input(Moving, flag: Verdict(min: 4), mover: mover));
        canary.AlertList.Should().Contain(new StageAlert("stage_pending", "drained:drain.canary_zero"));
    }

    [Fact]
    public async Task X4_runs_when_drained_and_stores_the_residue_and_the_drained_time_under_the_new_identity()
    {
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained(0, ("a", 3)) };

        var plan = await DecideAsync(Input(Moving, flag: Verdict(min: 4), mover: mover));

        Takes(plan, "X4", "S4", "fwd", 1);
        plan.Spec!.PostFields.Should().Contain(("res:a", "3")).And.Contain(f => f.Field == StateFields.DrainedAt);
    }

    [Fact]
    public async Task The_canary_over_its_threshold_sets_an_automatic_hold_and_a_clean_pass_clears_it()
    {
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained(canary: 6) };
        var held = await DecideAsync(Input(Moving, flag: Verdict(min: 4), mover: mover));
        held.AutoHoldSet.Should().Be("canary");
        held.Kind.Should().Be(StepKind.None, "the new hold pauses the step in the same pass");

        mover.Report = FakeMoverStatus.Drained(canary: 0);
        var cleared = await DecideAsync(Input(Moving with { HoldAuto = "canary" }, flag: Verdict(min: 3), mover: mover));
        cleared.AutoHoldClear.Should().BeTrue();
    }

    [Fact]
    public async Task An_operator_hold_is_cleared_by_no_clean_pass()
    {
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained() };

        var plan = await DecideAsync(Input(Moving with { HoldOp = "wait" }, flag: Verdict(min: 4), mover: mover));

        plan.Should().Match<StepPlan>(p => p.Kind == StepKind.None && !p.AutoHoldClear && p.AlertList.Single().Detail == "op:wait");
    }

    [Fact]
    public async Task A_node_without_the_key_move_tokens_sets_an_automatic_hold_and_it_clears_once_the_cause_is_gone()
    {
        var noKm = await DecideAsync(Input(Freeze, [Advert("n1", acks: Freeze), Advert("n2", km: false, acks: Freeze)]));
        noKm.AutoHoldSet.Should().Be("no_km:n2");
        noKm.Kind.Should().Be(StepKind.None);

        var noKmg = await DecideAsync(Input(Moving, [Advert("n1", acks: Moving), Advert("n2", kmg: false, acks: Moving)]));
        noKmg.AutoHoldSet.Should().Be("no_kmg:n2");

        var gone = await DecideAsync(Input(Freeze with { HoldAuto = "no_km:n2" }, flag: Verdict(min: 2)));
        gone.AutoHoldClear.Should().BeTrue();
    }

    [Fact]
    public async Task Every_fresh_node_acking_the_stored_identity_is_recorded_as_allAck_once()
    {
        var plan = await DecideAsync(Input(Moving, flag: Verdict(min: 3)));
        plan.AllAck.Should().BeTrue();

        var again = await DecideAsync(Input(Moving with { AllAck = Moving.Id }, flag: Verdict(min: 3)));
        again.AllAck.Should().BeFalse();
    }

    [Fact]
    public async Task X5_needs_the_flag_at_5_a_stored_close_consent_and_every_close_gate_and_names_the_ones_that_fail()
    {
        var soaked = Drained.With((StateFields.DrainedAt, "0")) with { Residue = new Dictionary<string, long> { ["a"] = 2 } };
        var consented = soaked.Approved(ApprovalKinds.Close);
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained(), Inventory = new Dictionary<string, long> { ["a"] = 2 } };
        var confirmed = Verdict(min: 5, confirmed: TimeSpan.FromMinutes(2));

        var plan = await DecideAsync(Input(consented, flag: confirmed, mover: mover));

        Takes(plan, "X5", "S5", "fwd", 1);
        plan.Spec!.RecheckViews.Should().BeTrue();
        plan.Spec.PostFields.Should().ContainSingle(f => f.Field == StateFields.ClosedAt);

        var noConsent = await DecideAsync(Input(soaked, flag: confirmed, mover: mover));
        noConsent.AlertList.Should().ContainSingle(a => a.Code == "close_pending").Which.Detail.Should().Be("close.consent:no_consent");
    }

    [Fact]
    public async Task Waits_for_fresh_adverts_only_and_names_the_ones_still_on_S3()
    {
        var soaked = Drained.With((StateFields.DrainedAt, "0")) with { Residue = new Dictionary<string, long> { ["a"] = 0 } };
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained() };

        var plan = await DecideAsync(Input(soaked.Approved(ApprovalKinds.Close), [Advert("n1", fl: 5, acks: soaked), Advert("n2", fl: 5, acks: Moving)], Verdict(min: 5, confirmed: TimeSpan.FromMinutes(3)), mover: mover));

        plan.AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.acks:n2");
    }

    [Fact]
    public async Task Close_refuses_unmet_gates_and_force_overrides_only_the_soak_and_canary()
    {
        var soaked = Drained.With((StateFields.DrainedAt, "0")) with { Residue = new Dictionary<string, long> { ["a"] = 2 } };
        var good = new FakeMoverStatus { Report = FakeMoverStatus.Drained(), Inventory = new Dictionary<string, long> { ["a"] = 2 } };
        var confirmed = Verdict(min: 5, confirmed: TimeSpan.FromMinutes(2));
        var consented = soaked.Approved(ApprovalKinds.Close);

        var early = soaked.With((StateFields.DrainedAt, DateTimeOffset.UnixEpoch.AddDays(29).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))).Approved(ApprovalKinds.Close);
        (await DecideAsync(Input(early, flag: confirmed, mover: good))).AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.soak:soak");

        var canary = new FakeMoverStatus { Report = FakeMoverStatus.Drained(canary: 2), Inventory = good.Inventory };
        (await DecideAsync(Input(consented, flag: confirmed, mover: canary))).AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.canary:canary");

        var bypass = new[] { Advert("n1", fl: 5, acks: soaked), Advert("n2", fl: 5, acks: soaked, byp: true) };
        (await DecideAsync(Input(consented, bypass, confirmed, mover: good))).AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.node_readiness:n2");

        var grown = new FakeMoverStatus { Report = FakeMoverStatus.Drained(), Inventory = new Dictionary<string, long> { ["a"] = 3 } };
        (await DecideAsync(Input(consented, flag: confirmed, mover: grown))).AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.inventory:a");

        (await DecideAsync(Input(consented, flag: Verdict(min: 5, confirmed: TimeSpan.FromSeconds(30)), mover: good))).AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.views:views_not_confirmed");

        var forced = soaked.With((StateFields.DrainedAt, DateTimeOffset.UnixEpoch.AddDays(29).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))).Approved(ApprovalKinds.CloseForce);
        var plan = await DecideAsync(Input(forced, flag: confirmed, mover: canary));
        Takes(plan, "X5", "S5", "fwd", 1);
        plan.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("gate_override", "close.soak,close.canary"));

        var stillBlocked = await DecideAsync(Input(forced, bypass, confirmed, mover: canary));
        stillBlocked.AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.node_readiness:n2");
    }

    [Fact]
    public async Task Needs_residue_known_for_every_cache_and_a_NoOrigin_cache_with_residue_has_no_override()
    {
        var unknown = Drained.With((StateFields.DrainedAt, "0"));
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained() };
        var confirmed = Verdict(min: 5, confirmed: TimeSpan.FromMinutes(2));

        var plan = await DecideAsync(Input(unknown.Approved(ApprovalKinds.Close), flag: confirmed, mover: mover));
        plan.AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.consent:residue_unknown:a");

        var withResidue = unknown with { Residue = new Dictionary<string, long> { ["a"] = 4 } };
        var noOrigin = await DecideAsync(Input(withResidue.Approved(ApprovalKinds.Close), flag: confirmed, mover: mover, facts: new FakeGroupFacts { NoOrigin = true }));
        noOrigin.AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.consent:noorigin_residue:a");

        var refill = await DecideAsync(Input(withResidue.Approved(ApprovalKinds.Close), flag: confirmed, mover: new FakeMoverStatus { Report = mover.Report, Inventory = withResidue.Residue! }, facts: new FakeGroupFacts { NoOrigin = true, AcceptRefill = true }));
        Takes(refill, "X5", "S5", "fwd", 1);
    }

    [Fact]
    public async Task A_stored_close_consent_is_void_when_the_residue_it_covers_changes_or_the_state_moves()
    {
        var soaked = Drained.With((StateFields.DrainedAt, "0")) with { Residue = new Dictionary<string, long> { ["a"] = 2 } };
        var consented = soaked.Approved(ApprovalKinds.Close);
        var changed = consented with { Residue = new Dictionary<string, long> { ["a"] = 1 } };
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained(), Inventory = new Dictionary<string, long> { ["a"] = 1 } };
        var confirmed = Verdict(min: 5, confirmed: TimeSpan.FromMinutes(2));

        (await DecideAsync(Input(changed, flag: confirmed, mover: mover))).AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.consent:no_consent");

        var moved = consented with { Ep = 99, Id = "other" };
        (await DecideAsync(Input(moved, flag: confirmed, mover: mover))).AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.consent:no_consent");
    }

    [Fact]
    public async Task X4_and_X5_are_never_taken_without_the_flag_asking()
    {
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained(), Inventory = new Dictionary<string, long>() };
        var soaked = Drained.With((StateFields.DrainedAt, "0")) with { Residue = new Dictionary<string, long> { ["a"] = 0 } };

        (await DecideAsync(Input(Moving, flag: Verdict(min: 3), mover: mover))).Kind.Should().Be(StepKind.None);
        (await DecideAsync(Input(soaked.Approved(ApprovalKinds.Close), flag: Verdict(min: 4, confirmed: TimeSpan.FromHours(1)), mover: mover))).Kind.Should().Be(StepKind.None);
    }

    [Fact]
    public async Task X13_is_one_step_back_after_the_lower_view_persisted_and_a_hold_pauses_it()
    {
        var plan = await DecideAsync(Input(Drained.With((StateFields.DrainedAt, "0")), flag: Verdict(min: 3, lowered: 3)));
        Takes(plan, "X13", "S3", "fwd", 1);
        plan.Spec!.NewMoveId.Should().BeTrue("the cursors restart from zero");

        var held = await DecideAsync(Input((Drained with { HoldOp = "x" }).With((StateFields.DrainedAt, "0")), flag: Verdict(min: 3, lowered: 3)));
        held.Kind.Should().Be(StepKind.None);
    }

    [Fact]
    public async Task A_hold_pauses_X2_X3_X4_and_X5_and_never_the_way_back()
    {
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained() };
        var facts = new FakeGroupFacts { NoOrigin = true };

        (await DecideAsync(Input(Ready with { HoldOp = "x" }, facts: facts))).Kind.Should().Be(StepKind.None);
        (await DecideAsync(Input(Freeze with { HoldOp = "x" }))).Kind.Should().Be(StepKind.None);
        (await DecideAsync(Input(Moving with { HoldOp = "x" }, flag: Verdict(min: 4), mover: mover))).Kind.Should().Be(StepKind.None);

        var back = await DecideAsync(Input(Moving with { HoldOp = "x" }, flag: Verdict(min: 3, lowered: 1), mover: mover));
        Takes(back, "X7", "S1", "rev", 1);
        var six = await DecideAsync(Input(Ready with { HoldOp = "x" }, flag: Verdict(min: 3, lowered: 2)));
        Takes(six, "X6", "S0", "none", 0);
    }

    [Fact]
    public async Task A_jump_from_2_to_5_walks_S1_then_S2_then_S3_then_waits_for_drained_then_S4_then_waits_for_the_consent()
    {
        var facts = new FakeGroupFacts { NoOrigin = true, ReleaseFloorAttested = true };
        var mover = new FakeMoverStatus();
        var five = Verdict(min: 5);

        Takes(await DecideAsync(Input(ReadyOff(), flag: five, facts: facts)), "X1", "S1", "fwd", 0);
        Takes(await DecideAsync(Input(Ready, flag: five, facts: facts)), "X2", "S2", "fwd", 0);
        Takes(await DecideAsync(Input(Freeze, flag: five, facts: facts)), "X3", "S3", "fwd", 1);
        (await DecideAsync(Input(Moving, flag: five, facts: facts, mover: mover))).AlertList.Single().Detail.Should().Be("drained:drain.confirming_pass,drain.skipped_accounted");
        mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        Takes(await DecideAsync(Input(Moving, flag: five, facts: facts, mover: mover)), "X4", "S4", "fwd", 1);
        var atFour = await DecideAsync(Input(Drained.With((StateFields.DrainedAt, "0")) with { Residue = new Dictionary<string, long> { ["a"] = 0 } }, flag: Verdict(min: 5, confirmed: TimeSpan.FromHours(1)), facts: facts, mover: mover));
        atFour.Kind.Should().Be(StepKind.None);
        atFour.AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.consent:no_consent");
    }

    private static Task<StepPlan> DecideAsync(StepInput input) => StepPlanner.DecideAsync(input, Defaults);

    private static void Takes(StepPlan plan, string code, string state, string direction, int nPop) =>
        plan.Should().Match<StepPlan>(p => p.Kind == StepKind.Transit && p.Spec!.Code == code && p.Spec.State == state && p.Spec.Direction == direction && p.Spec.NPop == nPop);
}
