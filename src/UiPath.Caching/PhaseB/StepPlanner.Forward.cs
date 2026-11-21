namespace UiPath.Caching.PhaseB;

internal static partial class StepPlanner
{
    private static async Task<StepPlan> StageStepAsync(StepInput i, StageDriverOptions options, StepSide side)
    {
        var state = i.State;
        return (state.State, state.Direction) switch
        {
            ("S0", _) => await FromOffAsync(i, options, side).ConfigureAwait(false),
            ("S1", "fwd") => await FromReadyAsync(i, options, side).ConfigureAwait(false),
            ("S2", "fwd") => await FromFreezeAsync(i, options, side).ConfigureAwait(false),
            ("S3", "fwd") => await FromMovingAsync(i, options, side).ConfigureAwait(false),
            ("S4", "fwd") => await FromDrainedAsync(i, options, side).ConfigureAwait(false),
            ("S5", _) => FromClosed(i, options),
            ("S1", "rev") => FromReverseReady(i),
            ("S3", "rev") => await FromReverseMovingAsync(i).ConfigureAwait(false),
            ("S4", "rev") => FromReverseDrained(i, side),
            _ => StepPlan.Wait("unknown_state", $"{state.State}:{state.Direction}"),
        };
    }

    // X1.
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

        var spec = new TransitionSpec("X1", "S1", "fwd", 0, NewMoveId: true, TakeClaim: i.Facts.TakesClaim(i.Group));
        var plan = new StepPlan(StepKind.Transit, spec);
        return overrides.Count == 0 ? plan : plan.With(new StageAlert("gate_override", string.Join(',', overrides)));
    }

    // X6, or X2 for a group with a NoOrigin cache, or the S1 branch of X3.
    private static async Task<StepPlan> FromReadyAsync(StepInput i, StageDriverOptions options, StepSide side)
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
            return StepPlan.Wait("stage_pending", $"ack:{Outstanding(i)}");
        }

        var noOrigin = i.Facts.HasNoOrigin(i.Group, i.State.CacheIds);
        if (!noOrigin && i.ViewStable < options.AckStable)
        {
            return StepPlan.Wait("stage_pending", "ack_stable");
        }

        if (await StartGates.ReplicasAsync(i).ConfigureAwait(false) is { } preflight)
        {
            return StepPlan.Wait("replica_unhealthy", preflight.Reason);
        }

        return noOrigin
            ? new StepPlan(StepKind.Transit, new TransitionSpec("X2", "S2", "fwd", 0))
            : new StepPlan(StepKind.Transit, new TransitionSpec("X3", "S3", "fwd", 1, Pre: PreStep.NativeCensus));
    }

    // X6, or X3 from the S2 barrier.
    private static async Task<StepPlan> FromFreezeAsync(StepInput i, StageDriverOptions options, StepSide side)
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
            return StepPlan.Wait("stage_pending", $"ack:{Outstanding(i)}");
        }

        if (await StartGates.ReplicasAsync(i).ConfigureAwait(false) is { } preflight)
        {
            return StepPlan.Wait("replica_unhealthy", preflight.Reason);
        }

        return new StepPlan(StepKind.Transit, new TransitionSpec("X3", "S3", "fwd", 1, Pre: PreStep.NativeCensus));
    }

    // X7, or X4 once DRAINED holds.
    private static async Task<StepPlan> FromMovingAsync(StepInput i, StageDriverOptions options, StepSide side)
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
        var pending = new List<string>();
        if (drain is not { Passed: true })
        {
            pending.Add("D1D2");
        }

        if (i.Fresh.Any(a => a.PromotionUnits(i.Group) != 0))
        {
            pending.Add("D3");
        }

        if (drain is { Canary: > 0 })
        {
            pending.Add("D4");
        }

        if (pending.Count > 0)
        {
            return StepPlan.Wait("stage_pending", $"drained:{string.Join(',', pending)}");
        }

        if (await StartGates.ReplicasAsync(i).ConfigureAwait(false) is { } preflight)
        {
            return StepPlan.Wait("replica_unhealthy", preflight.Reason);
        }

        var post = new List<(string, string)> { (StateFields.DrainedAt, Number(i.Now.ToUnixTimeMilliseconds())) };
        post.AddRange(drain!.Residue.Select(r => (StateFields.ResiduePrefix + r.Key, Number(r.Value))));
        return new StepPlan(StepKind.Transit, new TransitionSpec("X4", "S4", "fwd", 1, PostFields: post));
    }

    // X7, X13, or X5 once the CLOSED gates hold.
    private static async Task<StepPlan> FromDrainedAsync(StepInput i, StageDriverOptions options, StepSide side)
    {
        if (i.Flag.Lowered is <= 2)
        {
            return Reverse7(i, options);
        }

        if (i.Flag.Lowered == 3)
        {
            return side.Hold is not null ? StepPlan.Nothing : new StepPlan(StepKind.Transit, new TransitionSpec("X13", "S3", "fwd", 1, NewMoveId: true));
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
        if (failures.Failures.Count == 0 && await CloseGates.CensusAsync(i).ConfigureAwait(false) is { } census)
        {
            failures.Failures.Add(census);
        }

        if (failures.Failures.Count > 0)
        {
            return StepPlan.Wait("close_pending", string.Join(',', failures.Failures));
        }

        if (await StartGates.ReplicasAsync(i).ConfigureAwait(false) is { } preflight)
        {
            return StepPlan.Wait("replica_unhealthy", preflight.Reason);
        }

        var plan = new StepPlan(StepKind.Transit, new TransitionSpec("X5", "S5", "fwd", 1, RecheckViews: true, PostFields: [(StateFields.ClosedAt, Number(i.Now.ToUnixTimeMilliseconds()))]));
        return failures.Overrides.Count == 0 ? plan : plan.With(new StageAlert("gate_override", string.Join(',', failures.Overrides)));
    }

    // No way out of S5 but the operator's abandon, for groups whose caches all accept a refill.
    private static StepPlan FromClosed(StepInput i, StageDriverOptions options)
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
