namespace UiPath.Caching.KeyMove;

internal sealed partial class StageDriver
{
    internal const string ViewsFell = "the views fell before the commit";
    internal const string ClaimRefused = "the move claim refused to open";

    private async Task<TransitionResult?> ExecuteAsync(string group, StateRecord state, StepInput input, StepPlan plan, GroupMemory memory, CancellationToken cancellationToken)
    {
        await SideEffectsAsync(group, state, plan, cancellationToken).ConfigureAwait(false);
        if (!services.HoldsLease(group))
        {
            return null;
        }

        return plan.Kind switch
        {
            StepKind.Transit => await TransitAsync(group, state, input, plan, memory, cancellationToken).ConfigureAwait(false),
            StepKind.Abandon => await AbandonAsync(group, state, input, cancellationToken).ConfigureAwait(false),
            StepKind.Resume => await ResumeAsync(group, state, input, plan.SkipBarriers, true, cancellationToken).ConfigureAwait(false),
            StepKind.ColdStart => await ColdStartAsync(group, state, input, cancellationToken).ConfigureAwait(false),
            StepKind.RebuildCaches => await RebuildCachesAsync(group, state, plan, cancellationToken).ConfigureAwait(false),
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
            await services.Store.SetHoldAsync(group, HoldKind.Automatic, reason, Fence(group)).ConfigureAwait(false);
        }
        else if (plan.AutoHoldClear)
        {
            if (Fence(group) is { } fence)
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
            // T3 of the canary is stored with the acknowledgement itself, so one never exists without the other.
            long? t3 = state.State == KeyMoveStates.Moving ? time.GetUtcNow().ToUnixTimeMilliseconds() : null;
            await services.Store.AllAckAsync(group, state.Id, Fence(group), t3).ConfigureAwait(false);
        }

        if (plan.FieldList.Count > 0)
        {
            await services.Scripts.FieldsAsync(services.Names.State(group), state.Id, plan.FieldList, [], Fence(group)).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    // X12 in ready or barrier uses the cache set fresh nodes agree on. Every node acknowledges the new identity before X3.
    private async Task<TransitionResult?> RebuildCachesAsync(string group, StateRecord state, StepPlan plan, CancellationToken cancellationToken)
    {
        if (services.Upkeep is null || plan.Caches is null)
        {
            return null;
        }

        var sequence = new SequenceOptions(Ledger: services.Ledger, HoldsLease: () => services.HoldsLease(group), Fence: Fence(group));
        return await services.Upkeep.RebuildAsync(group, state, plan.Caches, sequence, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransitionResult?> TransitAsync(string group, StateRecord state, StepInput input, StepPlan plan, GroupMemory memory, CancellationToken cancellationToken)
    {
        var spec = plan.Spec!;
        if (!await PrepareAsync(group, state, spec, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var token = state.Claim;
        if (spec.TakeClaim)
        {
            if (await AcquireClaimAsync(group, cancellationToken).ConfigureAwait(false) is not { } acquired)
            {
                return null;
            }

            token = acquired;
        }

        var ep = state.Ep + 1;
        var target = CreateTransitTarget(group, state, spec, token, ep);
        var sequence = SequenceFor(group, spec, token, ep);
        var result = await services.Sequence.RunAsync(group, StateExpectation.Holding(state.Ep, state.Id, 1), target, sequence, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == TransitionOutcome.StateChanged && result.Detail is ViewsFell or ClaimRefused)
        {
            return await AbandonAsync(group, state with { Ep = ep, Id = target.Id, Durable = false, Prev = target.Prev, State = spec.State, Direction = spec.Direction, Claim = token }, input, cancellationToken).ConfigureAwait(false);
        }

        if (result.Outcome == TransitionOutcome.Completed)
        {
            CountOverrides(group, plan.OverriddenGates ?? []);
            await AfterCompletedAsync(group, spec, token, ep, target.Id, cancellationToken).ConfigureAwait(false);
        }
        else if (result.Outcome is TransitionOutcome.BarrierStuck or TransitionOutcome.CommitIncomplete or TransitionOutcome.CommitUncertain)
        {
            memory.Seed($"{ep}:{target.Id}", time);
        }

        return result;
    }

    private async Task<string?> AcquireClaimAsync(string group, CancellationToken cancellationToken)
    {
        // Persist the token before PEND so a retry recognizes a write whose reply was lost.
        if (await services.Claim.AcquireTokenAsync(group, Fence(group), cancellationToken).ConfigureAwait(false) is not { } token)
        {
            Notify(group, "claim_failed", "claim_token");
            return null;
        }

        var pended = Settle(group, await services.Claim.PendAsync(group, token, services.Facts.ClaimDescriptor(group), Fence(group), cancellationToken).ConfigureAwait(false));
        if (pended == ClaimOutcome.Done)
        {
            return token;
        }

        Notify(group, pended == ClaimOutcome.Refused ? "start_blocked" : "claim_failed", "claim_held");
        return null;
    }

    private StateTarget CreateTransitTarget(string group, StateRecord state, TransitionSpec spec, string token, long ep)
    {
        var id = MoveScripts.NewNonce();
        var (generationSet, generationClear) = Generations.Of(spec.Binding, state, id);
        return new(
            spec.State,
            spec.Direction,
            spec.NPop,
            ep,
            id,
            StateRecord.FormatPrev(state.State, state.Direction, state.NPop, state.Ep, state.Id),
            time.GetUtcNow().ToUnixTimeMilliseconds(),
            MoveId: spec.NewMoveId ? ep : null,
            Claim: spec.TakeClaim ? token : null,
            Move: spec.TakeClaim ? services.Facts.ClaimDescriptor(group) : null,
            Clear: spec.TakeClaim ? [StateFields.PendToken, StateFields.PendUncertain, .. generationClear] : generationClear,
            Set: generationSet);
    }

    private SequenceOptions SequenceFor(string group, TransitionSpec spec, string token, long ep)
    {
        var release = spec.ReleaseClaim && token.Length > 0 ? $"{token}:{ep}:{KeyMoveStates.NotStarted}" : string.Empty;
        Func<CancellationToken, Task<TransitionResult?>>? beforeCommit = null;
        if (spec.TakeClaim)
        {
            beforeCommit = async ct => await services.Claim.OpenAsync(group, token, restore: false, Fence(group), ct).ConfigureAwait(false) switch
            {
                ClaimOutcome.Done => null,
                ClaimOutcome.DoneUnconfirmed => new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "the move claim open is not on a replica"),
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
            Fence: Fence(group),
            BeforeCommit: beforeCommit,
            BeforeDurable: release.Length == 0 ? null : ct => CertifyAsync(group, token, ep, ct),
            AfterMarker: spec.TakeClaim ? (marker, ct) => services.Claim.CommitGuardAsync(group, token, marker, ct) : null);
    }

    private async Task<TransitionResult?> CertifyAsync(string group, string token, long ep, CancellationToken cancellationToken) =>
        Settle(group, await services.Claim.CertifyAsync(group, token, ep, KeyMoveStates.NotStarted, Fence(group), cancellationToken).ConfigureAwait(false)) switch
        {
            ClaimOutcome.Failed => new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "the move claim certificate could not be written"),
            _ => null,
        };

    // The inventory and the sweep that come before a transition; false when the step must wait. A purge runs only while the state still holds the identity it was decided under and this node still holds the lease.
    private async Task<bool> PrepareAsync(string group, StateRecord state, TransitionSpec spec, CancellationToken cancellationToken)
    {
        if (spec.Pre == PreStep.SweepNative)
        {
            if (await GuardAsync(group, state.Id).ConfigureAwait(false) is not { } sweep)
            {
                return false;
            }

            await services.Data.PurgeNativeAsync(group, sweep, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (spec.Pre != PreStep.NativeInventory)
        {
            return true;
        }

        // N is empty by construction in ready and barrier. Any key there is junk: delete it with PurgeData and recount.
        if (await services.Data.CountNativeAsync(group, cancellationToken).ConfigureAwait(false) > 0)
        {
            if (await GuardAsync(group, state.Id) is not { } guard)
            {
                return false;
            }

            await services.Data.PurgeNativeAsync(group, guard, cancellationToken).ConfigureAwait(false);
            if (await services.Data.CountNativeAsync(group, cancellationToken).ConfigureAwait(false) > 0)
            {
                Notify(group, "stage_pending", "native_keys_remain");
                return false;
            }
        }

        return true;
    }

    // Null, with an alert, when the state moved on or the lease was taken over: nothing destructive may run.
    private async Task<DataGuard?> GuardAsync(string group, string id)
    {
        var fence = Fence(group);
        var held = await services.Scripts.GuardAsync(services.Names.State(group), id, fence).ConfigureAwait(false);
        if (held == MoveScripts.Written)
        {
            return new DataGuard(id, fence);
        }

        Notify(group, held == MoveScripts.LeaseNotHeld ? "lease_lost" : "state_moved", "purge refused");
        return null;
    }

    // The evidence a Start consumed goes into the state under the identity of the move, fenced, so no later Start on any node can use it again.
    private async Task RecordStartAsync(string group, string id, CancellationToken cancellationToken)
    {
        var entries = services.Facts.StartTaken(group);
        if (entries.Count == 0 || await services.Store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is not { } current || current.Id != id)
        {
            return;
        }

        var joined = RegisteredGroupFacts.Join(current.Field(StateFields.Attested), entries);
        await services.Scripts.FieldsAsync(services.Names.State(group), id, [(StateFields.Attested, joined)], [], Fence(group)).ConfigureAwait(false);
    }

    private async Task AfterCompletedAsync(string group, TransitionSpec spec, string token, long ep, string id, CancellationToken cancellationToken)
    {
        if (spec.Code == "X1")
        {
            await RecordStartAsync(group, id, cancellationToken).ConfigureAwait(false);
        }

        if (spec.PostFields is { Count: > 0 } || spec.PostDelete is { Count: > 0 })
        {
            // The reverse inventory is evidence for the release only when it was written under the identity it was taken for.
            IReadOnlyList<(string Field, string Value)> fields = spec.Code == "X9" ? [.. spec.PostFields ?? [], (StateFields.ResidueId, id)] : spec.PostFields ?? [];
            await services.Scripts.FieldsAsync(services.Names.State(group), id, fields, spec.PostDelete ?? [], Fence(group)).ConfigureAwait(false);
        }

        if (spec.ReleaseClaim && token.Length > 0 && Settle(group, await services.Claim.ReleaseIdleAsync(group, token, Fence(group), cancellationToken).ConfigureAwait(false)) == ClaimOutcome.Failed)
        {
            Notify(group, "claim_release_failed", $"{token}:{ep}");
        }
    }

    private async Task<TransitionResult?> AbandonAsync(string group, StateRecord state, StepInput input, CancellationToken cancellationToken)
    {
        var acknowledged = input.Fresh.Any(a => a.Acknowledges(group, state));
        var ep = state.Ep + 1;
        var token = state.Claim;
        var abandoning = StepPlanner.Classify(state) == "X1" && token.Length > 0;

        // Only an open claim has a certificate to give; one that never opened is released idle and its token dropped from the state.
        var status = abandoning ? await services.Claim.StatusAsync(group, token, cancellationToken).ConfigureAwait(false) : ClaimStatus.Absent;
        var spec = new TransitionSpec("X12", KeyMoveStates.NotStarted, "none", 0, WayBack: true, ReleaseClaim: status == ClaimStatus.Open);
        var result = await services.Sequence.AbandonAsync(group, acknowledged, time.GetUtcNow().ToUnixTimeMilliseconds(), SequenceFor(group, spec, token, ep), cancellationToken).ConfigureAwait(false);
        if (result.Outcome == TransitionOutcome.AbandonRefused)
        {
            // The commit began or a node acknowledged: the transition is completed, and the flag is read again from the new state.
            return await ResumeAsync(group, state, input, false, false, cancellationToken).ConfigureAwait(false);
        }

        if (result.Outcome == TransitionOutcome.Completed && abandoning && status != ClaimStatus.Absent)
        {
            Settle(group, await services.Claim.ReleaseIdleAsync(group, token, Fence(group), cancellationToken).ConfigureAwait(false));
            if (status == ClaimStatus.Pending && await services.Store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is { } abandoned)
            {
                await services.Scripts.FieldsAsync(services.Names.State(group), abandoned.Id, [], [StateFields.Claim], Fence(group)).ConfigureAwait(false);
            }
        }

        return result;
    }

    private async Task<TransitionResult?> ResumeAsync(string group, StateRecord state, StepInput input, bool skipBarriers, bool recheck, CancellationToken cancellationToken)
    {
        var kind = StepPlanner.Classify(state);
        var wayBack = kind is "X6" or "X7" || (state is { State: KeyMoveStates.Moving or KeyMoveStates.Drained, Direction: "rev" }) || state.State == KeyMoveStates.NotStarted;
        var into0 = state.State == KeyMoveStates.NotStarted && state.Claim.Length > 0 && await services.Claim.StatusAsync(group, state.Claim, cancellationToken).ConfigureAwait(false) == ClaimStatus.Open;
        var spec = new TransitionSpec(kind ?? "X12", state.State, state.Direction, state.NPop, WayBack: wayBack, TakeClaim: kind == "X1" && state.Claim.Length > 0, ReleaseClaim: into0, RecheckViews: recheck && kind == "X5" && state.Field(StateFields.CommitBegun) != state.Id);
        var sequence = SequenceFor(group, spec, state.Claim, state.Ep) with { SkipBarriers = skipBarriers };
        var result = await services.Sequence.ResumeAsync(group, sequence, cancellationToken).ConfigureAwait(false);
        if (recheck && result.Outcome == TransitionOutcome.StateChanged && result.Detail is ViewsFell or ClaimRefused)
        {
            // Before the commit nothing was acted on, so the transition is abandoned; the abandon falls back to completion once the commit began.
            return await AbandonAsync(group, state, input, cancellationToken).ConfigureAwait(false);
        }

        if (result.Outcome == TransitionOutcome.Completed && into0)
        {
            Settle(group, await services.Claim.ReleaseIdleAsync(group, state.Claim, Fence(group), cancellationToken).ConfigureAwait(false));
        }

        if (result.Outcome == TransitionOutcome.Completed && kind == "X1")
        {
            // An X1 that was stuck and then completed is a Start taken, as much as one that completed at once.
            await RecordStartAsync(group, state.Id, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
