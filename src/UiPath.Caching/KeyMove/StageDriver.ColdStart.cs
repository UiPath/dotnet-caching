namespace UiPath.Caching.KeyMove;

internal sealed partial class StageDriver
{
    private const string NoClaim = "-";

    // X11. A stored marker on the state records that the operator's abandon began, so a leader that takes over repeats the missing step.
    private async Task<TransitionResult?> ColdStartAsync(string group, StateRecord state, StepInput input, CancellationToken cancellationToken)
    {
        var key = services.Names.State(group);
        var marker = state.Field(StateFields.ColdStart);
        if (state.State == KeyMoveStates.Closed)
        {
            if (marker.Length == 0)
            {
                marker = state.Claim.Length > 0 ? state.Claim : NoClaim;
                if (await services.Scripts.FieldsAsync(key, state.Id, [(StateFields.ColdStart, marker)], [], Fence(group)).ConfigureAwait(false) != MoveScripts.Written)
                {
                    return null;
                }
            }

            // The claim stays open until the sweep stores lswept.
            if (marker != NoClaim && state.Swept == state.Id && Settle(group, await services.Claim.ReopenAsync(group, marker, Fence(group), cancellationToken).ConfigureAwait(false)) != ClaimOutcome.Done)
            {
                Notify(group, "abandon_refused", "the move claim could not be reopened");
                return null;
            }

            var ep = state.Ep + 1;
            var target = new StateTarget(KeyMoveStates.NotStarted, "none", 0, ep, MoveScripts.NewNonce(), StateRecord.FormatPrev(state.State, state.Direction, state.NPop, state.Ep, state.Id), time.GetUtcNow().ToUnixTimeMilliseconds(), Clear: [StateFields.Fg, StateFields.Rg]);
            return await services.Sequence.RunAsync(group, StateExpectation.Holding(state.Ep, state.Id, 1), target, new SequenceOptions(Ledger: services.Ledger, HoldsLease: () => services.HoldsLease(group), Fence: Fence(group)), cancellationToken).ConfigureAwait(false);
        }

        return await PurgeAfterFlipAsync(group, state, marker, input, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransitionResult?> FinishPurgeAsync(string group, StateRecord state, string marker, CancellationToken cancellationToken)
    {
        var key = services.Names.State(group);
        if (marker != NoClaim)
        {
            if (Settle(group, await services.Claim.CertifyAsync(group, marker, state.Ep, KeyMoveStates.NotStarted, Fence(group), cancellationToken).ConfigureAwait(false)) == ClaimOutcome.Failed)
            {
                Notify(group, "claim_release_failed", marker);
                return null;
            }

            await services.Scripts.FieldsAsync(key, state.Id, [(StateFields.Release, $"{marker}:{state.Ep}:{KeyMoveStates.NotStarted}")], [], Fence(group)).ConfigureAwait(false);
            if (Settle(group, await services.Claim.ReleaseIdleAsync(group, marker, Fence(group), cancellationToken).ConfigureAwait(false)) == ClaimOutcome.Failed)
            {
                Notify(group, "claim_release_failed", marker);
                return null;
            }
        }

        await services.Scripts.FieldsAsync(key, state.Id, [], [StateFields.ColdStart], Fence(group)).ConfigureAwait(false);
        return new TransitionResult(TransitionOutcome.Completed);
    }

    // Wait for acknowledgements and connection recycling so old writes cannot arrive after the purge.
    private async Task<TransitionResult?> PurgeAfterFlipAsync(string group, StateRecord state, string marker, StepInput input, CancellationToken cancellationToken)
    {
        var waiting = input.Fresh.Where(a => !a.Acknowledges(group, state) || a.Recycled(group) != state.Ep).Select(a => a.Node).ToArray();
        if (input.Fresh.Count == 0 || waiting.Length > 0)
        {
            Notify(group, "stage_pending", $"recycle:{string.Join(',', waiting)}");
            return null;
        }

        if (await GuardAsync(group, state.Id).ConfigureAwait(false) is not { } guard)
        {
            return null;
        }

        await services.Data.PurgeNativeAsync(group, guard, cancellationToken).ConfigureAwait(false);

        // Recheck the lease before purging legacy keys.
        if (await GuardAsync(group, state.Id).ConfigureAwait(false) is not { } legacyGuard)
        {
            return null;
        }

        await services.Data.PurgeLegacyAsync(group, legacyGuard, cancellationToken).ConfigureAwait(false);
        return await FinishPurgeAsync(group, state, marker, cancellationToken).ConfigureAwait(false);
    }
}
