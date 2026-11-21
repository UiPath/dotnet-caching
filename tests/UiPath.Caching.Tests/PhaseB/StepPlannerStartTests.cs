using UiPath.Caching.PhaseB;
using static UiPath.Caching.Tests.PhaseB.PlannerKit;

namespace UiPath.Caching.Tests.PhaseB;

// X1 and the gates G1 to G14 (T-B21 gate part): each gate refuses and passes.
public class StepPlannerStartTests
{
    [Fact]
    public async Task Every_gate_green_and_every_view_at_3_starts_X1_with_the_move_id_and_no_claim_for_a_group_that_takes_none()
    {
        var plan = await DecideAsync(Input(ReadyOff()));

        Started(plan);
        plan.Spec!.TakeClaim.Should().BeFalse();
        plan.AlertList.Should().BeEmpty();
    }

    [Fact]
    public async Task A_router_group_takes_the_move_claim_at_X1()
    {
        var plan = await DecideAsync(Input(ReadyOff(), facts: new FakeGroupFacts { Claims = true, ReleaseFloorAttested = true }));

        Started(plan);
        plan.Spec!.TakeClaim.Should().BeTrue();
    }

    [Fact]
    public async Task Without_a_view_at_3_nothing_starts_and_one_node_asking_names_the_nodes_still_below()
    {
        var quiet = await DecideAsync(Input(ReadyOff(), Fleet(fl: 2), Verdict(min: 2)));
        quiet.Kind.Should().Be(StepKind.None);
        quiet.AlertList.Should().BeEmpty();

        var fresh = new[] { Advert("n1", fl: 3), Advert("n2", fl: 2) };
        var pending = await DecideAsync(Input(ReadyOff(), fresh, Verdict(min: 2)));
        pending.Kind.Should().Be(StepKind.None);
        pending.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("stage_pending", "flag_view:n2"));
    }

    [Fact]
    public async Task A_pod_that_has_not_read_the_flag_blocks_moving_forward()
    {
        var fresh = new[] { Advert("n1", fl: 3), Advert("n2", fl: null) };

        var plan = await DecideAsync(Input(ReadyOff(), fresh, Verdict(min: 3, missing: true)));

        plan.Kind.Should().Be(StepKind.None);
        plan.AlertList.Should().ContainSingle().Which.Detail.Should().Be("flag_view:n2");
    }

    [Fact]
    public async Task G1_a_node_without_pb_blocks_the_start()
    {
        var fresh = new[] { Advert("n1"), Advert("n2", pb: false) };

        Blocked(await DecideAsync(Input(ReadyOff(), fresh)), "G1");
    }

    [Fact]
    public async Task G2_a_node_that_is_not_pinned_or_reports_bypass_blocks_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", pin: false)])), "G2");
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", byp: true)])), "G2");
    }

    [Fact]
    public async Task G3_an_incomplete_node_list_or_no_fresh_advert_blocks_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), facts: new FakeGroupFacts { ReleaseFloorAttested = true, NodeListSettled = false })), "G3");
        (await DecideAsync(Input(ReadyOff(), [], Verdict(fresh: 0, min: null)))).Kind.Should().Be(StepKind.None, "no advert, not even this node's own, asks for anything");
    }

    [Fact]
    public async Task G4_an_advert_of_a_foreign_scope_blocks_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", scope: "other")])), "G4");
    }

    [Fact]
    public async Task G5_the_release_floor_is_attested_by_configuration_or_by_the_stored_start_consent()
    {
        var withoutFloor = new FakeGroupFacts();
        Blocked(await DecideAsync(Input(ReadyOff(), facts: withoutFloor)), "G5");

        var consented = await DecideAsync(Input(ReadyOff(ApprovalKinds.Start), facts: withoutFloor));
        Started(consented);
        consented.AlertList.Should().BeEmpty("the attestation is no override");
    }

    [Fact]
    public async Task G6_a_view_that_changed_within_the_upgrade_hold_blocks_unless_the_exact_override_is_stored()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), stable: TimeSpan.FromMinutes(119))), "G6");

        var plan = await DecideAsync(Input(ReadyOff(ApprovalKinds.StartUnsettled), stable: TimeSpan.FromMinutes(5)));
        Started(plan);
        plan.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("gate_override", ApprovalKinds.StartUnsettled));
    }

    [Fact]
    public async Task G7_a_node_with_a_stale_snapshot_blocks_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", snap: 61)])), "G7");
        Started(await DecideAsync(Input(ReadyOff(), [Advert("n1", snap: 4), Advert("n2", snap: 60)])));
    }

    [Fact]
    public async Task G8_a_dry_run_within_seven_days_is_needed_or_the_stored_skip()
    {
        var none = State();
        Blocked(await DecideAsync(Input(none)), "G8");

        var old = State(fields: (StateFields.DryRun, new DryRunStamp(DateTimeOffset.UnixEpoch.AddDays(22), 1, "old").Encode()));
        Blocked(await DecideAsync(Input(old)), "G8");

        var skipped = State().Approved(ApprovalKinds.StartNoDryRun).Approved(ApprovalKinds.StartMemory);
        var plan = await DecideAsync(Input(skipped));
        Started(plan);
        plan.AlertList.Single().Detail.Should().Be($"{ApprovalKinds.StartNoDryRun},{ApprovalKinds.StartMemory}");
    }

    [Fact]
    public async Task G9_the_memory_plan_must_fit_or_the_override_is_stored()
    {
        var facts = new FakeGroupFacts { ReleaseFloorAttested = true, Fits = false };
        Blocked(await DecideAsync(Input(ReadyOff(), facts: facts)), "G9");

        Started(await DecideAsync(Input(ReadyOff(ApprovalKinds.StartMemory), facts: facts)));
    }

    [Fact]
    public async Task G10_an_unsettled_format_state_blocks_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), facts: new FakeGroupFacts { ReleaseFloorAttested = true, Settled = false })), "G10");
    }

    [Fact]
    public async Task G11_a_node_that_does_not_list_the_group_blocks_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", pbg: false)])), "G11");
    }

    [Fact]
    public async Task G12_a_server_that_fails_the_protocol_probe_blocks_the_start_and_the_probe_carries_the_eviction_approval()
    {
        var refused = new PreflightResult([new PreflightFailure("eviction_policy", "m"), new PreflightFailure("replica_unhealthy", "m")]);
        var plan = await DecideAsync(Input(ReadyOff(), preflight: refused));

        Blocked(plan, "G12");
        plan.AlertList.Single().Detail.Should().Be("G12:eviction_policy,replica_unhealthy");
    }

    [Fact]
    public async Task G12_asks_the_probe_with_the_stored_eviction_approval()
    {
        bool? asked = null;
        var input = Input(ReadyOff(ApprovalKinds.ControlRecordsEvictable)) with
        {
            Preflight = approved =>
            {
                asked = approved;
                return Task.FromResult(new PreflightResult([]));
            },
        };

        Started(await DecideAsync(input));

        asked.Should().BeTrue();
    }

    [Fact]
    public async Task G13_adverts_that_disagree_with_the_stored_cache_set_block_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", ca: "a,b")])), "G13");
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", ca: null)])), "G13");
    }

    [Fact]
    public async Task G14_a_native_key_blocks_the_start_with_no_override_and_the_census_runs_only_when_the_other_gates_hold()
    {
        var data = new FakeGroupData { Native = 3 };
        var plan = await DecideAsync(Input(ReadyOff(ApprovalKinds.Start, ApprovalKinds.StartUnsettled, ApprovalKinds.StartMemory, ApprovalKinds.StartNoDryRun), data: data));

        Blocked(plan, "G14");

        var blockedElsewhere = new FakeGroupData { Native = 3 };
        await DecideAsync(Input(ReadyOff(), [Advert("n1", pb: false)], data: blockedElsewhere));
        blockedElsewhere.Calls.Should().BeEmpty("a cheap gate already refused");
    }

    [Fact]
    public async Task A_hold_pauses_the_start_and_a_stored_consent_is_void_once_the_state_has_another_identity()
    {
        var held = ReadyOff() with { HoldOp = "wait" };
        var plan = await DecideAsync(Input(held));
        plan.Kind.Should().Be(StepKind.None);
        plan.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("held", "op:wait"));

        var consented = ReadyOff(ApprovalKinds.Start, ApprovalKinds.StartUnsettled);
        var moved = consented with { Ep = consented.Ep + 1, Id = "other" };
        Blocked(await DecideAsync(Input(moved, facts: new FakeGroupFacts(), stable: TimeSpan.FromMinutes(1))), "G5");
    }

    private static Task<StepPlan> DecideAsync(StepInput input) => StepPlanner.DecideAsync(input, Defaults);

    private static void Started(StepPlan plan) =>
        plan.Should().Match<StepPlan>(p => p.Kind == StepKind.Transit && p.Spec!.Code == "X1" && p.Spec.State == "S1" && p.Spec.Direction == "fwd" && p.Spec.NPop == 0 && p.Spec.NewMoveId);

    private static void Blocked(StepPlan plan, string gate)
    {
        plan.Kind.Should().Be(StepKind.None);
        plan.AlertList.Should().ContainSingle(a => a.Code == "start_blocked").Which.Detail.Should().Contain(gate + ":");
    }
}
