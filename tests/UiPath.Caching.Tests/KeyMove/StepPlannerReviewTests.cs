using System.Globalization;
using UiPath.Caching.KeyMove;
using static UiPath.Caching.Tests.KeyMove.PlannerKit;

namespace UiPath.Caching.Tests.KeyMove;

public class StepPlannerReviewTests
{
    private static readonly StateRecord CopyingBack = State("S3", "rev", ep: 16, nPop: 1, caches: "a+b", prev: "S1:rev:1:15:id15", residue: new Dictionary<string, long> { ["a"] = 0, ["b"] = 0, ["gone"] = 7 });
    private static readonly StateRecord CopiedBack = State("S4", "rev", ep: 17, nPop: 1, caches: "a+b", prev: "S3:rev:1:16:id16").With((StateFields.ResidueId, "id17"));
    private static readonly StateRecord Copying = State("S3", ep: 13, nPop: 1, prev: "S2:fwd:0:12:id12");

    [Fact]
    public async Task X9_carries_the_reverse_inventory_into_S4_replacing_the_forward_counts_and_dropping_the_caches_it_no_longer_reports()
    {
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained(0, ("a", 3), ("b", 0)) };

        var plan = await StepPlanner.DecideAsync(Input(CopyingBack, mover: mover), Defaults);

        plan.Spec!.Code.Should().Be("X9");
        plan.Spec.PostFields.Should().BeEquivalentTo([("res:a", "3"), ("res:b", "0")]);
        plan.Spec.PostDelete.Should().Equal("res:gone");
    }

    [Fact]
    public async Task X10_waits_while_the_residue_of_any_cache_is_unknown_and_the_consent_hashes_the_reverse_counts()
    {
        var unknown = CopiedBack with { Residue = new Dictionary<string, long> { ["a"] = 0 } };
        var wait = await StepPlanner.DecideAsync(Input(unknown.Approved(ApprovalKinds.Release), mover: new FakeMoverStatus { Report = FakeMoverStatus.Drained(0, ("a", 0)) }), Defaults);
        wait.Kind.Should().Be(StepKind.None);
        wait.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("stage_pending", "release_residue_unknown:b"));

        var forwardZero = CopiedBack with { Residue = new Dictionary<string, long> { ["a"] = 0, ["b"] = 0 } };
        var approved = forwardZero.Approved(ApprovalKinds.Release);
        var reverseInventory = new FakeMoverStatus { Report = FakeMoverStatus.Drained(0, ("a", 4), ("b", 0)) };

        var plan = await StepPlanner.DecideAsync(Input(approved, mover: reverseInventory), Defaults);

        plan.Kind.Should().Be(StepKind.None, "an inventory that differs from the counts the consent covers is stored first, and X10 does not run on the old consent");
        plan.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("stage_pending", "release_inventory_changed"));
        plan.Fields.Should().Contain(("res:a", "4"), "the new inventory is stored, which voids the consent");
        (await StepPlanner.DecideAsync(Input(approved, mover: new FakeMoverStatus { Report = FakeMoverStatus.Drained(0, ("a", 0), ("b", 0)) }), Defaults)).Kind.Should().Be(StepKind.Transit, "the consent covers the counts a re-inventory finds");
        var stored = approved.With(("res:a", "4")) with { Residue = new Dictionary<string, long> { ["a"] = 4, ["b"] = 0 } };
        (await StepPlanner.DecideAsync(Input(stored, mover: reverseInventory), Defaults)).AlertList.Single().Detail.Should().Be("release_consent");
    }

    [Fact]
    public async Task A_release_waits_when_the_post_write_of_X9_was_lost_even_with_complete_forward_counts_and_a_valid_consent()
    {
        var lost = (CopiedBack with { Residue = new Dictionary<string, long> { ["a"] = 0, ["b"] = 0 } }).With((StateFields.ResidueId, "id9")).Approved(ApprovalKinds.Release);
        var mover = new FakeMoverStatus();

        var none = await StepPlanner.DecideAsync(Input(lost, mover: mover), Defaults);
        none.Kind.Should().Be(StepKind.None);
        none.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("stage_pending", "release_inventory_pending"));
        none.Fields.Should().BeEmpty("no report is no inventory");

        mover.Report = FakeMoverStatus.Drained(0, ("a", 0));
        var partial = await StepPlanner.DecideAsync(Input(lost, mover: mover), Defaults);
        partial.Kind.Should().Be(StepKind.None);
        partial.Fields.Should().BeEmpty("a report that lacks a cache is not a complete inventory");

        mover.Report = FakeMoverStatus.Drained(0, ("a", 0), ("b", 0));
        var complete = await StepPlanner.DecideAsync(Input(lost, mover: mover), Defaults);
        complete.Kind.Should().Be(StepKind.None, "the inventory is written under this identity first");
        complete.Fields.Should().Contain((StateFields.ResidueId, "id17"));
    }

    [Fact]
    public async Task A_release_runs_once_the_counts_carry_the_identity_marker_and_the_consent_covers_them()
    {
        var written = (CopiedBack with { Residue = new Dictionary<string, long> { ["a"] = 2, ["b"] = 0 } }).Approved(ApprovalKinds.Release);
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained(0, ("a", 2), ("b", 0)) };

        var plan = await StepPlanner.DecideAsync(Input(written, mover: mover), Defaults);

        plan.Spec!.Code.Should().Be("X10");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(0, true)]
    public async Task A_canary_hold_clears_only_after_a_pass_that_counted_none(long canary, bool clears)
    {
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained(canary) };

        var plan = await StepPlanner.DecideAsync(Input(Copying with { HoldAuto = "canary" }, flag: Verdict(min: 3), mover: mover), Defaults);

        plan.AutoHoldClear.Should().Be(clears);
    }

    [Fact]
    public async Task A_canary_hold_needs_a_clean_forward_report_and_clears_in_reverse_without_a_report()
    {
        var none = await StepPlanner.DecideAsync(Input(Copying with { HoldAuto = "canary" }, flag: Verdict(min: 3), mover: new FakeMoverStatus()), Defaults);
        none.AutoHoldClear.Should().BeFalse();

        var reverse = await StepPlanner.DecideAsync(Input(CopiedBack with { HoldAuto = "canary", Residue = new Dictionary<string, long> { ["a"] = 0, ["b"] = 0 } }, mover: new FakeMoverStatus()), Defaults);
        reverse.AutoHoldClear.Should().BeTrue();
    }

    [Fact]
    public async Task A_hold_for_a_node_without_the_key_move_token_clears_as_soon_as_the_cause_is_gone_without_a_inventory()
    {
        var plan = await StepPlanner.DecideAsync(Input(Copying with { HoldAuto = "no_km:n2" }, flag: Verdict(min: 3), mover: new FakeMoverStatus()), Defaults);

        plan.AutoHoldClear.Should().BeTrue();
    }

    [Fact]
    public async Task An_automatic_hold_left_in_S0_by_the_last_run_is_cleared_so_a_raise_is_not_blocked_until_Resume()
    {
        var off = State("S0", "none", ep: 18, prev: "S4:rev:1:17:id17", holdAuto: "no_km:n2");

        var plan = await StepPlanner.DecideAsync(Input(off, flag: Verdict(min: 2)), Defaults);

        plan.AutoHoldClear.Should().BeTrue();
        (await StepPlanner.DecideAsync(Input(off with { HoldOp = "x" }, flag: Verdict(min: 2)), Defaults)).AutoHoldClear.Should().BeTrue("the operator hold is a different class and stays");
    }

    [Fact]
    public async Task A_forward_S4_without_residue_gets_it_from_a_master_inventory_unless_an_operator_hold_asks_for_a_inventory_first()
    {
        var copied = State("S4", ep: 14, nPop: 1, prev: "S3:fwd:1:13:id13").With((StateFields.CopiedAt, "0"));
        var mover = new FakeMoverStatus { Inventory = new Dictionary<string, long> { ["a"] = 4 } };

        var plan = await StepPlanner.DecideAsync(Input(copied, flag: Verdict(min: 4), mover: mover), Defaults);

        plan.Fields.Should().Contain(("res:a", "4"));
        (await StepPlanner.DecideAsync(Input(copied with { HoldOp = "inventory first" }, flag: Verdict(min: 4), mover: mover), Defaults)).Fields.Should().NotContain(f => f.Field == "res:a");
        (await StepPlanner.DecideAsync(Input(copied with { Residue = new Dictionary<string, long> { ["a"] = 1 } }, flag: Verdict(min: 4), mover: mover), Defaults)).Fields.Should().NotContain(f => f.Field == "res:a");
    }

    [Fact]
    public async Task The_canary_must_stay_quiet_for_the_whole_soak_and_no_report_adds_no_evidence()
    {
        var soaked = State("S4", ep: 14, nPop: 1, prev: "S3:fwd:1:13:id13", residue: new Dictionary<string, long> { ["a"] = 0 }).With((StateFields.CopiedAt, "0"));
        var confirmed = Verdict(min: 5, confirmed: TimeSpan.FromMinutes(3));
        var recent = DateTimeOffset.UnixEpoch.AddDays(30).AddHours(-1).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var facts = new FakeGroupFacts { ReleaseFloorAttested = true };
        var options = new StageDriverOptions { CopiedSoak = TimeSpan.FromHours(6) };
        var mover = new FakeMoverStatus { Inventory = new Dictionary<string, long> { ["a"] = 0 } };

        var early = await StepPlanner.DecideAsync(Input(soaked.With((StateFields.CanaryAt, recent)).Approved(ApprovalKinds.Close), flag: confirmed, facts: facts, mover: mover), options);
        early.AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.canary:canary");

        var old = DateTimeOffset.UnixEpoch.AddDays(30).AddHours(-7).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        (await StepPlanner.DecideAsync(Input(soaked.With((StateFields.CanaryAt, old)).Approved(ApprovalKinds.Close), flag: confirmed, facts: facts, mover: mover), options)).Spec!.Code.Should().Be("X5");
        (await StepPlanner.DecideAsync(Input(soaked.Approved(ApprovalKinds.Close), flag: confirmed, facts: facts, mover: mover), options)).Spec!.Code.Should().Be("X5", "no report and no canary on record");
    }

    [Fact]
    public async Task A_pass_that_counts_a_canary_key_records_when_so_the_soak_restarts()
    {
        var copying = State("S3", ep: 13, nPop: 1, prev: "S2:fwd:0:12:id12");
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained(canary: 2) };

        var plan = await StepPlanner.DecideAsync(Input(copying, flag: Verdict(min: 3), mover: mover), Defaults);

        plan.Fields.Should().Contain(f => f.Field == StateFields.CanaryAt);
    }

    [Fact]
    public async Task The_soak_is_capped_by_the_longest_source_ttl_of_the_group()
    {
        var drainedAt = DateTimeOffset.UnixEpoch.AddDays(30).AddHours(-2).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var state = State("S4", ep: 14, nPop: 1, prev: "S3:fwd:1:13:id13", residue: new Dictionary<string, long> { ["a"] = 0 }).With((StateFields.CopiedAt, drainedAt)).Approved(ApprovalKinds.Close);
        var mover = new FakeMoverStatus { Report = FakeMoverStatus.Drained(), Inventory = new Dictionary<string, long> { ["a"] = 0 } };
        var confirmed = Verdict(min: 5, confirmed: TimeSpan.FromMinutes(3));

        var long7 = await StepPlanner.DecideAsync(Input(state, flag: confirmed, mover: mover), Defaults);
        long7.AlertList.Single(a => a.Code == "close_pending").Detail.Should().Be("close.soak:soak");

        var capped = await StepPlanner.DecideAsync(Input(state, flag: confirmed, mover: mover, facts: new FakeGroupFacts { ReleaseFloorAttested = true, SourceTtl = TimeSpan.FromHours(1) }), Defaults);
        capped.Spec!.Code.Should().Be("X5");
    }
}
