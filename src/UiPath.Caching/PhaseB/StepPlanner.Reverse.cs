namespace UiPath.Caching.PhaseB;

internal static partial class StepPlanner
{
    // X6: S1 or S2 back to S0; nothing was moved. A group that holds a claim releases it on the way.
    private static StepPlan Reverse6(StepInput i) =>
        new(StepKind.Transit, new TransitionSpec("X6", "S0", "none", 0, WayBack: true, ReleaseClaim: i.State.Claim.Length > 0));

    // X7: S3 or S4 to S1(rev) through the reverse gates, or a stored forced reverse.
    private static StepPlan Reverse7(StepInput i, StageDriverOptions options)
    {
        var (failures, forced) = StartGates.Reverse(i, options);
        if (failures.Count > 0)
        {
            return StepPlan.Wait("reverse_blocked", string.Join(',', failures));
        }

        var plan = new StepPlan(StepKind.Transit, new TransitionSpec("X7", "S1", "rev", 1, NewMoveId: true, WayBack: true));
        return forced ? plan.With(new StageAlert("gate_override", ApprovalKinds.ForceReverse)) : plan;
    }

    // X8: S1(rev) is the barrier for NoOrigin caches; every fresh advert acknowledges it.
    private static StepPlan FromReverseReady(StepInput i) =>
        AllAcknowledge(i)
            ? new StepPlan(StepKind.Transit, new TransitionSpec("X8", "S3", "rev", 1, WayBack: true))
            : StepPlan.Wait("stage_pending", $"ack:{Outstanding(i)}");

    // X9: D1 to D3 with N as the source; no canary condition and no hold.
    private static async Task<StepPlan> FromReverseMovingAsync(StepInput i)
    {
        var drain = await i.Drain().ConfigureAwait(false);
        var pending = new List<string>();
        if (drain is not { Passed: true })
        {
            pending.Add("D1D2");
        }

        if (i.Fresh.Any(a => a.PromotionUnits(i.Group) != 0))
        {
            pending.Add("D3");
        }

        return pending.Count > 0
            ? StepPlan.Wait("stage_pending", $"drained:{string.Join(',', pending)}")
            : new StepPlan(StepKind.Transit, new TransitionSpec("X9", "S4", "rev", 1, WayBack: true));
    }

    // X10: after the stored release consent, N is swept and the group flips to S0.
    private static StepPlan FromReverseDrained(StepInput i) =>
        Approval.ValidFor(i.State, ApprovalKinds.Release) is null
            ? StepPlan.Wait("stage_pending", "release_consent")
            : new StepPlan(StepKind.Transit, new TransitionSpec("X10", "S0", "none", 0, WayBack: true, ReleaseClaim: i.State.Claim.Length > 0, Pre: PreStep.SweepNative));
}
