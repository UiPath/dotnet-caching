using UiPath.Caching.KeyMove;
using static UiPath.Caching.Tests.KeyMove.PlannerKit;

namespace UiPath.Caching.Tests.KeyMove;

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
    public async Task A_node_without_the_key_move_token_blocks_the_start()
    {
        var fresh = new[] { Advert("n1"), Advert("n2", km: false) };

        Blocked(await DecideAsync(Input(ReadyOff(), fresh)), "start.km_support");
    }

    [Fact]
    public async Task A_node_that_is_not_pinned_or_reports_bypass_blocks_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", pin: false)])), "start.pins");
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", byp: true)])), "start.pins");
    }

    [Fact]
    public async Task An_incomplete_node_list_or_no_fresh_advert_blocks_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), facts: new FakeGroupFacts { ReleaseFloorAttested = true, NodeListSettled = false })), "start.node_list");
        (await DecideAsync(Input(ReadyOff(), [], Verdict(fresh: 0, min: null)))).Kind.Should().Be(StepKind.None, "no advert, not even this node's own, asks for anything");
    }

    [Fact]
    public async Task An_advert_of_a_foreign_scope_blocks_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", scope: "other")])), "start.foreign_node");
    }

    [Fact]
    public async Task The_release_floor_is_attested_by_configuration_or_by_the_stored_start_consent()
    {
        var withoutFloor = new FakeGroupFacts();
        Blocked(await DecideAsync(Input(ReadyOff(), facts: withoutFloor)), "start.release_floor");

        var consented = await DecideAsync(Input(ReadyOff(ApprovalKinds.Start), facts: withoutFloor));
        Started(consented);
        consented.AlertList.Should().BeEmpty("the attestation is no override");
    }

    [Fact]
    public async Task A_view_that_changed_within_the_upgrade_hold_blocks_unless_the_exact_override_is_stored()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), stable: TimeSpan.FromMinutes(119))), "start.view_stable");

        var plan = await DecideAsync(Input(ReadyOff(ApprovalKinds.StartUnsettled), stable: TimeSpan.FromMinutes(5)));
        Started(plan);
        plan.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("gate_override", ApprovalKinds.StartUnsettled));
    }

    [Fact]
    public async Task A_node_with_a_stale_snapshot_blocks_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", snap: 61)])), "start.snapshot_freshness");
        Started(await DecideAsync(Input(ReadyOff(), [Advert("n1", snap: 4), Advert("n2", snap: 60)])));
    }

    [Fact]
    public async Task A_dry_run_within_seven_days_is_needed_or_the_stored_skip()
    {
        var none = State();
        Blocked(await DecideAsync(Input(none)), "start.dry_run");

        var old = State(fields: (StateFields.DryRun, new DryRunStamp(DateTimeOffset.UnixEpoch.AddDays(22), 1, "old").Encode()));
        Blocked(await DecideAsync(Input(old)), "start.dry_run");

        var skipped = State().Approved(ApprovalKinds.StartNoDryRun).Approved(ApprovalKinds.StartMemory);
        var plan = await DecideAsync(Input(skipped));
        Started(plan);
        plan.AlertList.Single().Detail.Should().Be($"{ApprovalKinds.StartNoDryRun},{ApprovalKinds.StartMemory}");
    }

    [Fact]
    public async Task The_memory_plan_must_fit_or_the_override_is_stored()
    {
        var facts = new FakeGroupFacts { ReleaseFloorAttested = true, Fits = false };
        Blocked(await DecideAsync(Input(ReadyOff(), facts: facts)), "start.memory_headroom");

        Started(await DecideAsync(Input(ReadyOff(ApprovalKinds.StartMemory), facts: facts)));
    }

    [Fact]
    public async Task An_unsettled_format_state_blocks_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), facts: new FakeGroupFacts { ReleaseFloorAttested = true, Settled = false })), "start.format_settled");
    }

    [Fact]
    public async Task A_node_that_does_not_list_the_group_blocks_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", kmg: false)])), "start.group_listed");
    }

    [Fact]
    public async Task A_server_that_fails_the_protocol_probe_blocks_the_start_and_the_probe_carries_the_eviction_approval()
    {
        var refused = new PreflightResult([new PreflightFailure("eviction_policy", "m"), new PreflightFailure("replica_unhealthy", "m")]);
        var plan = await DecideAsync(Input(ReadyOff(), preflight: refused));

        Blocked(plan, "start.server_preflight");
        plan.AlertList.Single().Detail.Should().Be("start.server_preflight:eviction_policy,replica_unhealthy");
    }

    [Fact]
    public async Task Asks_the_probe_with_the_stored_eviction_approval()
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
    public async Task Adverts_that_disagree_with_the_stored_cache_set_block_the_start()
    {
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", ca: "a,b")])), "start.cache_set");
        Blocked(await DecideAsync(Input(ReadyOff(), [Advert("n1"), Advert("n2", ca: null)])), "start.cache_set");
    }

    [Fact]
    public async Task A_native_key_blocks_the_start_with_no_override_and_the_inventory_runs_only_when_the_other_gates_hold()
    {
        var data = new FakeGroupData { Native = 3 };
        var plan = await DecideAsync(Input(ReadyOff(ApprovalKinds.Start, ApprovalKinds.StartUnsettled, ApprovalKinds.StartMemory, ApprovalKinds.StartNoDryRun), data: data));

        Blocked(plan, "start.native_empty");

        var blockedElsewhere = new FakeGroupData { Native = 3 };
        await DecideAsync(Input(ReadyOff(), [Advert("n1", km: false)], data: blockedElsewhere));
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
        Blocked(await DecideAsync(Input(moved, facts: new FakeGroupFacts(), stable: TimeSpan.FromMinutes(1))), "start.release_floor");
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
