using UiPath.Caching.PhaseB;
using static UiPath.Caching.Tests.PhaseB.PlannerKit;

namespace UiPath.Caching.Tests.PhaseB;

// A transition at dur=0: completed or abandoned by where its sequence stands and what the flag asks (T-B51 flips).
public class StepPlannerFlightTests
{
    [Theory]
    [InlineData("S1", "fwd", "S0:none:0:11:id11", 3)]
    [InlineData("S2", "fwd", "S1:fwd:0:11:id11", 3)]
    [InlineData("S3", "fwd", "S2:fwd:0:11:id11", 3)]
    public async Task A_forward_transition_whose_view_fell_before_step_4_is_abandoned_without_waiting(string state, string direction, string prev, int level)
    {
        var plan = await DecideAsync(Input(InFlight(state, direction, prev), [Advert("n1", fl: level), Advert("n2", fl: level - 1)], Verdict(min: level - 1), durZero: TimeSpan.FromSeconds(1)));

        plan.Kind.Should().Be(StepKind.Abandon);
    }

    [Fact]
    public async Task X4_and_X5_in_flight_are_abandoned_when_a_view_fell_below_their_stage()
    {
        var x4 = InFlight("S4", "fwd", "S3:fwd:1:11:id11");
        (await DecideAsync(Input(x4, [Advert("n1", fl: 4), Advert("n2", fl: 3)], Verdict(min: 3)))).Kind.Should().Be(StepKind.Abandon);

        var x5 = InFlight("S5", "fwd", "S4:fwd:1:11:id11");
        (await DecideAsync(Input(x5, [Advert("n1", fl: 5), Advert("n2", fl: 4)], Verdict(min: 4)))).Kind.Should().Be(StepKind.Abandon, "X5 is abandoned when one view fell before step 4");
    }

    [Fact]
    public async Task A_transition_still_asked_is_resumed_only_after_DurRetry()
    {
        var x2 = InFlight("S2", "fwd", "S1:fwd:0:11:id11");

        (await DecideAsync(Input(x2, Fleet(fl: 3), Verdict(min: 3), durZero: TimeSpan.FromSeconds(9)))).Kind.Should().Be(StepKind.None);
        var resumed = await DecideAsync(Input(x2, Fleet(fl: 3), Verdict(min: 3), durZero: TimeSpan.FromSeconds(10)));
        resumed.Kind.Should().Be(StepKind.Resume);
        resumed.SkipBarriers.Should().BeFalse();
    }

    [Theory]
    [InlineData("S1", "fwd", "S0:none:0:11:id11")]
    [InlineData("S2", "fwd", "S1:fwd:0:11:id11")]
    [InlineData("S3", "fwd", "S2:fwd:0:11:id11")]
    public async Task A_forward_transition_before_step_4_is_not_resumed_while_a_pod_has_no_view_or_no_advert_is_fresh(string state, string direction, string prev)
    {
        var flight = InFlight(state, direction, prev);
        var missing = new[] { Advert("n1", fl: 3), Advert("n2", fl: null) };

        var blocked = await DecideAsync(Input(flight, missing, Verdict(min: 3, missing: true), durZero: TimeSpan.FromMinutes(5)));
        blocked.Kind.Should().Be(StepKind.None);
        blocked.AlertList.Should().ContainSingle().Which.Should().Be(new StageAlert("stage_pending", "flag_view:n2"));

        var nobody = await DecideAsync(Input(flight, [], Verdict(min: null, fresh: 0), durZero: TimeSpan.FromMinutes(5)));
        nobody.Kind.Should().Be(StepKind.None);

        var allMissing = await DecideAsync(Input(flight, [Advert("n1", fl: null)], Verdict(min: null, fresh: 1, missing: true), durZero: TimeSpan.FromMinutes(5)));
        allMissing.Kind.Should().Be(StepKind.None, "a pod that never read F15 is not a view at or above the stage");
    }

    [Fact]
    public async Task A_transition_whose_step_4_began_is_completed_even_when_pods_show_no_view_and_is_never_abandoned()
    {
        var begun = InFlight("S2", "fwd", "S1:fwd:0:11:id11").With((StateFields.CommitBegun, "id12"));

        var missing = await DecideAsync(Input(begun, [Advert("n1", fl: null)], Verdict(min: null, fresh: 1, missing: true), durZero: TimeSpan.FromSeconds(10)));
        missing.Kind.Should().Be(StepKind.Resume);

        var lowered = await DecideAsync(Input(begun, Fleet(fl: 1), Verdict(min: 1), durZero: TimeSpan.FromSeconds(10)));
        lowered.Kind.Should().Be(StepKind.Resume);
    }

    [Fact]
    public async Task X7_in_flight_is_abandoned_when_the_flag_is_raised_before_step_4_and_resumed_while_it_is_still_lowered()
    {
        var x7 = InFlight("S1", "rev", "S3:fwd:1:11:id11");

        (await DecideAsync(Input(x7, [Advert("n1", fl: 3), Advert("n2", fl: 3)], Verdict(min: 3), durZero: TimeSpan.FromSeconds(1)))).Kind.Should().Be(StepKind.Abandon);
        (await DecideAsync(Input(x7, [Advert("n1", fl: 3), Advert("n2", fl: 1)], Verdict(min: 1), durZero: TimeSpan.FromSeconds(11)))).Kind.Should().Be(StepKind.Resume);
    }

    [Fact]
    public async Task X6_in_flight_is_abandoned_when_the_flag_is_raised()
    {
        var x6 = InFlight("S0", "none", "S1:fwd:0:11:id11");

        (await DecideAsync(Input(x6, Fleet(fl: 3), Verdict(min: 3)))).Kind.Should().Be(StepKind.Abandon);
        (await DecideAsync(Input(x6, Fleet(fl: 2), Verdict(min: 2), durZero: TimeSpan.FromSeconds(30)))).Kind.Should().Be(StepKind.Resume);
    }

    [Theory]
    [InlineData("S3", "rev", "S1:rev:1:11:id11")]
    [InlineData("S4", "rev", "S3:rev:1:11:id11")]
    [InlineData("S0", "none", "S4:rev:1:11:id11")]
    [InlineData("S0", "none", "S5:fwd:1:11:id11")]
    public async Task A_reverse_in_flight_from_step_X8_on_is_never_abandoned_whatever_the_flag_does(string state, string direction, string prev)
    {
        var plan = await DecideAsync(Input(InFlight(state, direction, prev), Fleet(fl: 5), Verdict(min: 5), durZero: TimeSpan.FromSeconds(11)));

        plan.Kind.Should().Be(StepKind.Resume);
    }

    [Fact]
    public async Task X13_in_flight_is_abandoned_when_the_flag_goes_back_to_4_and_a_repair_is_always_completed()
    {
        var x13 = InFlight("S3", "fwd", "S4:fwd:1:11:id11");

        (await DecideAsync(Input(x13, Fleet(fl: 4), Verdict(min: 4)))).Kind.Should().Be(StepKind.Abandon);
        (await DecideAsync(Input(x13, Fleet(fl: 3), Verdict(min: 3), durZero: TimeSpan.FromSeconds(10)))).Kind.Should().Be(StepKind.Resume);

        var repair = InFlight("S3", "fwd", "S3:fwd:1:11:id11");
        (await DecideAsync(Input(repair, Fleet(fl: 5), Verdict(min: 5), durZero: TimeSpan.FromSeconds(10)))).Kind.Should().Be(StepKind.Resume);
    }

    [Fact]
    public async Task A_stored_skip_barrier_consent_resumes_with_the_barriers_skipped_and_only_for_that_identity()
    {
        var stuck = InFlight("S2", "fwd", "S1:fwd:0:11:id11");

        var plan = await DecideAsync(Input(stuck.Approved(ApprovalKinds.SkipBarrier), Fleet(fl: 3), Verdict(min: 3), durZero: TimeSpan.FromSeconds(10)));
        plan.Should().Match<StepPlan>(p => p.Kind == StepKind.Resume && p.SkipBarriers);

        var older = stuck.Approved(ApprovalKinds.SkipBarrier) with { Ep = 13, Id = "id13" };
        (await DecideAsync(Input(older, Fleet(fl: 3), Verdict(min: 3), durZero: TimeSpan.FromSeconds(10)))).SkipBarriers.Should().BeFalse();
    }

    [Fact]
    public async Task A_commit_mismatch_is_completed_never_abandoned_and_an_identity_conflict_starts_nothing()
    {
        var stuck = InFlight("S2", "fwd", "S1:fwd:0:11:id11");

        var completed = await DecideAsync(Input(stuck, Fleet(fl: 2), Verdict(min: 2), conflict: "commit_mismatch"));
        completed.Kind.Should().Be(StepKind.Resume);
        completed.AlertList.Should().ContainSingle().Which.Code.Should().Be("commit_mismatch");

        var conflict = await DecideAsync(Input(State("S1", ep: 11, prev: "S0:none:0:10:id10"), flag: Verdict(min: 5), conflict: "identity_conflict"));
        conflict.Kind.Should().Be(StepKind.None);
        conflict.AlertList.Should().ContainSingle().Which.Code.Should().Be("identity_conflict");
    }

    private static Task<StepPlan> DecideAsync(StepInput input) => StepPlanner.DecideAsync(input, Defaults);

    private static StateRecord InFlight(string state, string direction, string prev, long ep = 12) => State(state, direction, ep: ep, durable: false, nPop: 0, prev: prev);
}
