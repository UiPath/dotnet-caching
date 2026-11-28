namespace UiPath.Caching.KeyMove;

internal static partial class StepPlanner
{
    private const string ReplicaUnhealthy = "replica_unhealthy";

    private static async Task<StepPlan> StageStepAsync(StepInput i, StageDriverOptions options, StepSide side)
    {
        var state = i.State;
        return (state.State, state.Direction) switch
        {
            (KeyMoveStates.Off, _) => await FromOffAsync(i, options, side).ConfigureAwait(false),
            (KeyMoveStates.Preparing, "fwd") => await FromPreparingAsync(i, options, side).ConfigureAwait(false),
            (KeyMoveStates.Switching, "fwd") => await FromFreezeAsync(i, side).ConfigureAwait(false),
            (KeyMoveStates.Copying, "fwd") => await FromCopyingAsync(i, options, side).ConfigureAwait(false),
            (KeyMoveStates.Copied, "fwd") => await FromCopiedAsync(i, options, side).ConfigureAwait(false),
            (KeyMoveStates.Done, _) => FromDone(i),
            (KeyMoveStates.Preparing, "rev") => await FromReturningAsync(i, options).ConfigureAwait(false),
            (KeyMoveStates.Copying, "rev") => await FromCopyingBackAsync(i).ConfigureAwait(false),
            (KeyMoveStates.Copied, "rev") => FromCopiedBack(i, side),
            _ => StepPlan.Wait("unknown_state", $"{KeyMoveStates.NameOf(state.State, state.Direction)}:{state.Direction}"),
        };
    }

    private static async Task<StepPlan> FromOffAsync(StepInput i, StageDriverOptions options, StepSide side)
    {
        if (FlagBlocks(i, 3) is { } blocked)
        {
            return blocked;
        }

        if (side.Hold is not null)
        {
            return StepPlan.Nothing;
        }

        var (failures, overrides) = StartGates.Cheap(i, options);
        if (failures.Count == 0 && await StartGates.ServerAsync(i).ConfigureAwait(false) is { } server)
        {
            failures.Add(server);
        }

        if (failures.Count == 0 && await StartGates.NativeEmptyAsync(i).ConfigureAwait(false) is { } native)
        {
            failures.Add(native);
        }

        if (failures.Count > 0)
        {
            return StepPlan.Wait("start_blocked", string.Join(',', failures));
        }

        var spec = new TransitionSpec("X1", KeyMoveStates.Preparing, "fwd", 0, NewMoveId: true, TakeClaim: i.Facts.TakesClaim(i.Group), Binding: BindingChange.Mint);
        var plan = new StepPlan(StepKind.Transit, spec);
        return overrides.Count == 0 ? plan : plan.WithOverrides(string.Join(',', overrides.Select(o => o.Approval)), [.. overrides.Select(o => o.Gate)]);
    }

    private static async Task<StepPlan> FromPreparingAsync(StepInput i, StageDriverOptions options, StepSide side)
    {
        if (i.Flag.Lowered is <= 2)
        {
            return Reverse6(i);
        }

        if (FlagBlocks(i, 3) is { } blocked)
        {
            return blocked;
        }

        if (side.Hold is not null)
        {
            return StepPlan.Nothing;
        }

        if (!AllAcknowledge(i))
        {
            return StepPlan.Wait(StagePending, $"ack:{Outstanding(i)}");
        }

        var noOrigin = i.Facts.HasNoOrigin(i.Group, i.State.CacheIds);
        if (!noOrigin && i.ViewStable < options.AckStable)
        {
            return StepPlan.Wait(StagePending, "ack_stable");
        }

        if (await StartGates.ReplicasAsync(i).ConfigureAwait(false) is { } preflight)
        {
            return StepPlan.Wait(ReplicaUnhealthy, preflight.Reason);
        }

        return noOrigin
            ? new StepPlan(StepKind.Transit, new TransitionSpec("X2", KeyMoveStates.Switching, "fwd", 0))
            : new StepPlan(StepKind.Transit, new TransitionSpec("X3", KeyMoveStates.Copying, "fwd", 1, Pre: PreStep.NativeInventory));
    }

    private static async Task<StepPlan> FromFreezeAsync(StepInput i, StepSide side)
    {
        if (i.Flag.Lowered is <= 2)
        {
            return Reverse6(i);
        }

        if (FlagBlocks(i, 3) is { } blocked)
        {
            return blocked;
        }

        if (side.Hold is not null)
        {
            return StepPlan.Nothing;
        }

        if (!AllAcknowledge(i))
        {
            return StepPlan.Wait(StagePending, $"ack:{Outstanding(i)}");
        }

        if (await StartGates.ReplicasAsync(i).ConfigureAwait(false) is { } preflight)
        {
            return StepPlan.Wait(ReplicaUnhealthy, preflight.Reason);
        }

        return new StepPlan(StepKind.Transit, new TransitionSpec("X3", KeyMoveStates.Copying, "fwd", 1, Pre: PreStep.NativeInventory));
    }

    private static async Task<StepPlan> FromCopyingAsync(StepInput i, StageDriverOptions options, StepSide side)
    {
        if (i.Flag.Lowered is <= 2)
        {
            return Reverse7(i, options);
        }

        if (FlagBlocks(i, 4) is { } blocked)
        {
            return blocked;
        }

        if (side.Hold is not null)
        {
            return StepPlan.Nothing;
        }

        var drain = await i.Drain().ConfigureAwait(false);
        var pending = DrainFailures(i, drain);
        if (drain is { Canary: > 0 })
        {
            pending.Add(KeyMoveGateIds.DrainCanaryZero);
        }

        if (pending.Count > 0)
        {
            return StepPlan.Wait(StagePending, $"copied:{string.Join(',', pending)}");
        }

        if (await StartGates.ReplicasAsync(i).ConfigureAwait(false) is { } preflight)
        {
            return StepPlan.Wait(ReplicaUnhealthy, preflight.Reason);
        }

        var post = new List<(string, string)> { (StateFields.CopiedAt, Number(i.Now.ToUnixTimeMilliseconds())) };
        post.AddRange(drain!.Residue.Select(r => (StateFields.ResiduePrefix + r.Key, Number(r.Value))));
        return new StepPlan(StepKind.Transit, new TransitionSpec("X4", KeyMoveStates.Copied, "fwd", 1, PostFields: post));
    }

    private static List<string> DrainFailures(StepInput i, DrainReport? drain)
    {
        var pending = new List<string>();
        if (drain is not { Passed: true })
        {
            pending.Add(KeyMoveGateIds.DrainConfirmingPass);
            pending.Add(KeyMoveGateIds.DrainSkippedAccounted);
        }

        if (i.Fresh.Any(a => a.PromotionUnits(i.Group) != 0))
        {
            pending.Add(KeyMoveGateIds.DrainPromotionsIdle);
        }

        return pending;
    }

    private static async Task<StepPlan> FromCopiedAsync(StepInput i, StageDriverOptions options, StepSide side)
    {
        if (i.Flag.Lowered is <= 2)
        {
            return Reverse7(i, options);
        }

        if (i.Flag.Lowered == 3)
        {
            return side.Hold is not null ? StepPlan.Nothing : new StepPlan(StepKind.Transit, new TransitionSpec("X13", KeyMoveStates.Copying, "fwd", 1, NewMoveId: true));
        }

        if (FlagBlocks(i, 5) is { } blocked)
        {
            return blocked;
        }

        if (side.Hold is not null)
        {
            return StepPlan.Nothing;
        }

        var failures = await CloseGates.CheapAsync(i, options).ConfigureAwait(false);
        if (failures.Failures.Count == 0 && await CloseGates.InventoryAsync(i).ConfigureAwait(false) is { } inventory)
        {
            failures.Failures.Add(inventory);
        }

        if (failures.Failures.Count > 0)
        {
            return StepPlan.Wait("close_pending", string.Join(',', failures.Failures));
        }

        if (await StartGates.ReplicasAsync(i).ConfigureAwait(false) is { } preflight)
        {
            return StepPlan.Wait(ReplicaUnhealthy, preflight.Reason);
        }

        var plan = new StepPlan(StepKind.Transit, new TransitionSpec("X5", KeyMoveStates.Done, "fwd", 1, RecheckViews: true, PostFields: [(StateFields.DoneAt, Number(i.Now.ToUnixTimeMilliseconds()))]));
        return failures.Overrides.Count == 0 ? plan : plan.WithOverrides(string.Join(',', failures.Overrides), failures.Overrides);
    }

    // done permits only operator abandon, for groups whose caches all accept a refill.
    private static StepPlan FromDone(StepInput i)
    {
        if (Approval.ValidFor(i.State, ApprovalKinds.Abandon) is not null)
        {
            return i.Facts.AllAcceptRefill(i.Group, i.State.CacheIds)
                ? new StepPlan(StepKind.ColdStart)
                : StepPlan.Wait("abandon_refused", "a cache does not accept a refill");
        }

        return i.Flag.Lowered is not null ? StepPlan.Wait("flag_below_closed", i.Group) : StepPlan.Nothing;
    }
}
