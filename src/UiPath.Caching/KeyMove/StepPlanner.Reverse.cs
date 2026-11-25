namespace UiPath.Caching.KeyMove;

internal static partial class StepPlanner
{
    private const string StagePending = "stage_pending";

    // X6: S1 or S2 back to S0; nothing was moved. A group that holds a claim releases it on the way.
    private static StepPlan Reverse6(StepInput i) =>
        new(StepKind.Transit, new TransitionSpec("X6", "S0", "none", 0, WayBack: true, ReleaseClaim: i.State.Claim.Length > 0, Binding: BindingChange.ClearAll));

    // X7: S3 or S4 to S1(rev) through the reverse gates, or a stored forced reverse.
    private static StepPlan Reverse7(StepInput i, StageDriverOptions options)
    {
        var (failures, overrides) = StartGates.Reverse(i, options);
        if (failures.Count > 0)
        {
            return StepPlan.Wait("reverse_blocked", string.Join(',', failures));
        }

        var plan = new StepPlan(StepKind.Transit, new TransitionSpec("X7", "S1", "rev", 1, NewMoveId: true, WayBack: true, Binding: BindingChange.Retire));
        return overrides.Count == 0 ? plan : plan.WithOverrides(ApprovalKinds.ForceReverse, overrides);
    }

    // S1(rev) is the barrier for NoOrigin caches; every fresh advert acknowledges it. The retire gate: no marker of the retiring generation is left, or the operator's consent to leave them.
    private static async Task<StepPlan> FromReverseReadyAsync(StepInput i, StageDriverOptions options)
    {
        if (!AllAcknowledge(i))
        {
            return StepPlan.Wait(StagePending, $"ack:{Outstanding(i)}");
        }

        var plan = new StepPlan(StepKind.Transit, new TransitionSpec("X8", "S3", "rev", 1, WayBack: true, Binding: BindingChange.ClearRetiring));
        if (Approval.ValidFor(i.State, ApprovalKinds.RetireMarkers) is not null)
        {
            return plan.WithOverrides(ApprovalKinds.RetireMarkers, [KeyMoveGateIds.ReverseRetire]);
        }

        var verdict = await i.Retire().ConfigureAwait(false);
        if (verdict.Clear)
        {
            return plan;
        }

        // Blocked is said only by complete passes that found markers, and only after they kept finding them for RetireWait.
        if (verdict.Complete && verdict.BlockedFor >= options.RetireWait)
        {
            return StepPlan.Wait("reverse_blocked", $"retire:{verdict.Remaining}");
        }

        return StepPlan.Wait(StagePending, verdict.Complete ? $"retire:{verdict.Remaining}" : "retire:scanning");
    }

    // The reverse pass inventories native keys and is independent of the canary and holds.
    private static async Task<StepPlan> FromReverseMovingAsync(StepInput i)
    {
        var drain = await i.Drain().ConfigureAwait(false);
        var pending = DrainFailures(i, drain);
        if (pending.Count > 0)
        {
            return StepPlan.Wait(StagePending, $"drained:{string.Join(',', pending)}");
        }

        // The residue of the reverse is the inventory of N: it replaces whatever the forward move stored.
        var (post, delete) = ResidueFields(i.State, drain!.Residue);
        return new StepPlan(StepKind.Transit, new TransitionSpec("X9", "S4", "rev", 1, WayBack: true, PostFields: post, PostDelete: delete));
    }

    // Sweep only after a complete inventory under this identity. Changed counts invalidate release consent.
    private static StepPlan FromReverseDrained(StepInput i, StepSide side)
    {
        if (i.State.Field(StateFields.ResidueId) != i.State.Id)
        {
            return StepPlan.Wait(StagePending, "release_inventory_pending");
        }

        if (side.Fields.Any(f => f.Field.StartsWith(StateFields.ResiduePrefix, StringComparison.Ordinal)))
        {
            return StepPlan.Wait(StagePending, "release_inventory_changed");
        }

        if (i.State.CacheIds.Where(c => i.State.Residue is null || !i.State.Residue.ContainsKey(c)).ToArray() is { Length: > 0 } unknown)
        {
            return StepPlan.Wait(StagePending, $"release_residue_unknown:{string.Join(',', unknown)}");
        }

        if (Approval.ValidFor(i.State, ApprovalKinds.Release) is null)
        {
            return StepPlan.Wait(StagePending, "release_consent");
        }

        return new StepPlan(StepKind.Transit, new TransitionSpec("X10", "S0", "none", 0, WayBack: true, ReleaseClaim: i.State.Claim.Length > 0, Pre: PreStep.SweepNative, Binding: BindingChange.ClearAll));
    }

    private static (List<(string Field, string Value)> Set, List<string> Delete) ResidueFields(StateRecord state, IReadOnlyDictionary<string, long> residue)
    {
        var set = residue.Select(r => (StateFields.ResiduePrefix + r.Key, Number(r.Value))).ToList();
        var delete = (state.Residue?.Keys ?? []).Where(c => !residue.ContainsKey(c)).Select(c => StateFields.ResiduePrefix + c).ToList();
        return (set, delete);
    }
}
