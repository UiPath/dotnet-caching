using UiPath.Caching.KeyMove;
using static UiPath.Caching.Tests.KeyMove.PlannerKit;

namespace UiPath.Caching.Tests.KeyMove;

// The retire gate and the step out of S1 as the step logic decides it from what the gate saw.
public class StepPlannerRetireTests
{
    private static readonly StateRecord Reverse = State("S1", "rev", ep: 15, nPop: 1, prev: "S4:fwd:1:14:id14").With((StateFields.Rg, "gen-a"));

    [Fact]
    public async Task The_retire_step_goes_ahead_when_the_gate_is_clear()
    {
        Takes(await DecideAsync(Input(Reverse, retire: RetireVerdict.Done)), "X8", "S3", "rev", 1);
    }

    [Fact]
    public async Task An_unfinished_pass_never_says_reverse_blocked_however_long_it_has_been()
    {
        var plan = await DecideAsync(Input(Reverse, retire: new RetireVerdict(false, 0, Complete: false, BlockedFor: TimeSpan.FromHours(1))));

        plan.Kind.Should().Be(StepKind.None);
        plan.AlertList.Should().ContainSingle(a => a.Code == "stage_pending" && a.Detail == "retire:scanning");
    }

    [Fact]
    public async Task Complete_passes_that_found_markers_wait_and_say_reverse_blocked_only_after_RetireWait()
    {
        var early = await DecideAsync(Input(Reverse, retire: new RetireVerdict(false, 3, Complete: true, BlockedFor: TimeSpan.FromSeconds(59))));
        var late = await DecideAsync(Input(Reverse, retire: new RetireVerdict(false, 3, Complete: true, BlockedFor: TimeSpan.FromSeconds(60))));

        early.AlertList.Should().ContainSingle(a => a.Code == "stage_pending" && a.Detail == "retire:3");
        late.AlertList.Should().ContainSingle(a => a.Code == "reverse_blocked" && a.Detail == "retire:3");
        early.Kind.Should().Be(StepKind.None);
        late.Kind.Should().Be(StepKind.None);
    }

    [Fact]
    public async Task A_consent_given_under_another_identity_does_not_skip_the_gate_and_one_for_this_identity_does()
    {
        var blocked = new RetireVerdict(false, 3, Complete: true);
        var foreign = new Approval(ApprovalKinds.RetireMarkers, "9:another-identity", string.Empty, "op", "ok");
        var stale = Reverse.With((StateFields.ApprovalPrefix + ApprovalKinds.RetireMarkers, foreign.Encode()));

        (await DecideAsync(Input(stale, retire: blocked))).Kind.Should().Be(StepKind.None);
        var plan = await DecideAsync(Input(Reverse.Approved(ApprovalKinds.RetireMarkers), retire: blocked));

        Takes(plan, "X8", "S3", "rev", 1);
        plan.AlertList.Should().Contain(a => a.Code == "gate_override" && a.Detail == ApprovalKinds.RetireMarkers);
    }

    private static Task<StepPlan> DecideAsync(StepInput input) => StepPlanner.DecideAsync(input, new StageDriverOptions());

    private static void Takes(StepPlan plan, string code, string state, string direction, int nPop) =>
        plan.Should().Match<StepPlan>(p => p.Kind == StepKind.Transit && p.Spec!.Code == code && p.Spec.State == state && p.Spec.Direction == direction && p.Spec.NPop == nPop);
}
