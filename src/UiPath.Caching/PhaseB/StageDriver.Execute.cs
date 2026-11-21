namespace UiPath.Caching.PhaseB;

internal sealed partial class StageDriver
{
    internal const string ViewsFell = "the views fell before step 4";
    internal const string ClaimRefused = "the move claim refused to open";
    private const string RouterMove = "rt";

    private async Task<TransitionResult?> ExecuteAsync(string group, StateRecord state, StepInput input, StepPlan plan, GroupMemory memory, CancellationToken cancellationToken)
    {
        await SideEffectsAsync(group, state, plan, cancellationToken).ConfigureAwait(false);
        if (!services.HoldsLease(group))
        {
            return null;
        }

        return plan.Kind switch
        {
            StepKind.Transit => await TransitAsync(group, state, input, plan.Spec!, memory, cancellationToken).ConfigureAwait(false),
            StepKind.Abandon => await AbandonAsync(group, state, input, cancellationToken).ConfigureAwait(false),
            StepKind.Resume => await ResumeAsync(group, state, plan.SkipBarriers, cancellationToken).ConfigureAwait(false),
            StepKind.ColdStart => await ColdStartAsync(group, state, input, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    private async Task SideEffectsAsync(string group, StateRecord state, StepPlan plan, CancellationToken cancellationToken)
    {
        if (!services.HoldsLease(group))
        {
            return;
        }

        if (plan.AutoHoldSet is { } reason)
        {
            await services.Store.SetHoldAsync(group, HoldKind.Automatic, reason).ConfigureAwait(false);
        }
        else if (plan.AutoHoldClear)
        {
            if (services.Fence?.Invoke(group) is { } fence)
            {
                await services.Store.ClearAutomaticHoldAsync(group, fence).ConfigureAwait(false);
            }
            else
            {
                await services.Store.ClearHoldAsync(group, HoldKind.Automatic).ConfigureAwait(false);
            }
        }

        if (plan.AllAck)
        {
            await services.Store.AllAckAsync(group, state.Id, services.Fence?.Invoke(group)).ConfigureAwait(false);
        }

        if (plan.FieldList.Count > 0)
        {
            await services.Scripts.FieldsAsync(services.Names.State(group), state.Id, plan.FieldList, []).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<TransitionResult?> TransitAsync(string group, StateRecord state, StepInput input, TransitionSpec spec, GroupMemory memory, CancellationToken cancellationToken)
    {
        if (!await PrepareAsync(group, spec, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var token = state.Claim;
        if (spec.TakeClaim)
        {
            token = MoveScripts.NewNonce();
            var pended = await services.Claim.PendAsync(group, token, RouterMove, cancellationToken).ConfigureAwait(false);
            if (pended != ClaimOutcome.Done)
            {
                services.Alert?.Invoke(group, pended == ClaimOutcome.Refused ? "start_blocked" : "claim_failed", "claim_held");
                return null;
            }
        }

        var ep = state.Ep + 1;
        var target = new StateTarget(
            spec.State,
            spec.Direction,
            spec.NPop,
            ep,
            MoveScripts.NewNonce(),
            StateRecord.FormatPrev(state.State, state.Direction, state.NPop, state.Ep, state.Id),
            time.GetUtcNow().ToUnixTimeMilliseconds(),
            MoveId: spec.NewMoveId ? ep : null,
            Claim: spec.TakeClaim ? token : null,
            Move: spec.TakeClaim ? RouterMove : null);
        var sequence = SequenceFor(group, spec, token, ep);
        var result = await services.Sequence.RunAsync(group, StateExpectation.Holding(state.Ep, state.Id, 1), target, sequence, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == TransitionOutcome.StateChanged && result.Detail is ViewsFell or ClaimRefused)
        {
            return await AbandonAsync(group, state with { Ep = ep, Id = target.Id, Durable = false, Prev = target.Prev, State = spec.State, Direction = spec.Direction, Claim = token }, input, cancellationToken).ConfigureAwait(false);
        }

        if (result.Outcome == TransitionOutcome.Completed)
        {
            await AfterCompletedAsync(group, spec, token, ep, target.Id, cancellationToken).ConfigureAwait(false);
        }
        else if (result.Outcome is TransitionOutcome.BarrierStuck or TransitionOutcome.CommitIncomplete)
        {
            memory.Seed($"{ep}:{target.Id}", time);
        }

        return result;
    }

    private SequenceOptions SequenceFor(string group, TransitionSpec spec, string token, long ep)
    {
        var release = spec.ReleaseClaim && token.Length > 0 ? $"{token}:{ep}:S0" : string.Empty;
        Func<CancellationToken, Task<TransitionResult?>>? beforeCommit = null;
        if (spec.TakeClaim)
        {
            beforeCommit = async ct => await services.Claim.OpenAsync(group, token, restore: false, ct).ConfigureAwait(false) switch
            {
                ClaimOutcome.Done => null,
                ClaimOutcome.Refused => new TransitionResult(TransitionOutcome.StateChanged, Detail: ClaimRefused),
                _ => new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "the move claim could not be opened"),
            };
        }
        else if (spec.RecheckViews)
        {
            beforeCommit = async ct =>
            {
                var fresh = await FreshAsync(ct).ConfigureAwait(false);
                return fresh.Count == 0 || fresh.Any(a => a.FlagOf(group) is not { } v || v < 5) ? new TransitionResult(TransitionOutcome.StateChanged, Detail: ViewsFell) : null;
            };
        }

        return new SequenceOptions(
            WayBack: spec.WayBack,
            Release: release,
            Ledger: services.Ledger,
            HoldsLease: () => services.HoldsLease(group),
            Fence: services.Fence?.Invoke(group),
            BeforeCommit: beforeCommit,
            BeforeDurable: release.Length == 0 ? null : ct => CertifyAsync(group, token, ep, ct));
    }

    private async Task<TransitionResult?> CertifyAsync(string group, string token, long ep, CancellationToken cancellationToken) =>
        await services.Claim.CertifyAsync(group, token, ep, "S0", cancellationToken).ConfigureAwait(false) switch
        {
            ClaimOutcome.Failed => new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "the move claim certificate could not be written"),
            _ => null,
        };

    // The census and the sweep that come before a transition; false when the step must wait.
    private async Task<bool> PrepareAsync(string group, TransitionSpec spec, CancellationToken cancellationToken)
    {
        if (spec.Pre == PreStep.SweepNative)
        {
            await services.Data.PurgeNativeAsync(group, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (spec.Pre != PreStep.NativeCensus)
        {
            return true;
        }

        // N is empty by construction in S1 and S2, so a key found there is junk: delete it by PurgeData and count again.
        if (await services.Data.CountNativeAsync(group, cancellationToken).ConfigureAwait(false) > 0)
        {
            await services.Data.PurgeNativeAsync(group, cancellationToken).ConfigureAwait(false);
            if (await services.Data.CountNativeAsync(group, cancellationToken).ConfigureAwait(false) > 0)
            {
                services.Alert?.Invoke(group, "stage_pending", "native_keys_remain");
                return false;
            }
        }

        return true;
    }

    private async Task AfterCompletedAsync(string group, TransitionSpec spec, string token, long ep, string id, CancellationToken cancellationToken)
    {
        if (spec.PostFields is { Count: > 0 } post)
        {
            await services.Scripts.FieldsAsync(services.Names.State(group), id, post, []).ConfigureAwait(false);
        }

        if (spec.ReleaseClaim && token.Length > 0)
        {
            if (await services.Claim.ReleaseIdleAsync(group, token, cancellationToken).ConfigureAwait(false) == ClaimOutcome.Failed)
            {
                services.Alert?.Invoke(group, "claim_release_failed", $"{token}:{ep}");
            }
        }
    }

    private async Task<TransitionResult?> AbandonAsync(string group, StateRecord state, StepInput input, CancellationToken cancellationToken)
    {
        var acknowledged = input.Fresh.Any(a => a.Acknowledges(group, state));
        var ep = state.Ep + 1;
        var token = state.Claim;
        var abandoning = StepPlanner.Classify(state) == "X1" && token.Length > 0;
        var spec = new TransitionSpec("X12", "S0", "none", 0, WayBack: true, ReleaseClaim: abandoning);
        var result = await services.Sequence.AbandonAsync(group, acknowledged, time.GetUtcNow().ToUnixTimeMilliseconds(), SequenceFor(group, spec, token, ep), cancellationToken).ConfigureAwait(false);
        if (result.Outcome == TransitionOutcome.AbandonRefused)
        {
            // Step 4 began or a node acknowledged: the transition is completed, and the flag is read again from the new state.
            return await ResumeAsync(group, state, false, cancellationToken).ConfigureAwait(false);
        }

        if (result.Outcome == TransitionOutcome.Completed && abandoning)
        {
            await services.Claim.ReleaseIdleAsync(group, token, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<TransitionResult?> ResumeAsync(string group, StateRecord state, bool skipBarriers, CancellationToken cancellationToken)
    {
        var kind = StepPlanner.Classify(state);
        var wayBack = kind is "X6" or "X7" || (state is { State: "S3" or "S4", Direction: "rev" }) || state.State == "S0";
        var into0 = state.State == "S0" && state.Claim.Length > 0;
        var spec = new TransitionSpec(kind ?? "X12", state.State, state.Direction, state.NPop, WayBack: wayBack, TakeClaim: kind == "X1" && state.Claim.Length > 0, ReleaseClaim: into0);
        var options = SequenceFor(group, spec, state.Claim, state.Ep) with { SkipBarriers = skipBarriers };
        var result = await services.Sequence.ResumeAsync(group, options, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == TransitionOutcome.Completed && into0)
        {
            await services.Claim.ReleaseIdleAsync(group, state.Claim, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
