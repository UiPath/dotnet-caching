namespace UiPath.Caching.PhaseB;

internal sealed partial class StageDriver
{
    private const string NoClaim = "-";

    // X11. A stored marker on the state records that the operator's abandon began, so a leader that takes over repeats the missing step.
    private async Task<TransitionResult?> ColdStartAsync(string group, StateRecord state, StepInput input, CancellationToken cancellationToken)
    {
        var key = services.Names.State(group);
        var marker = state.Field(StateFields.ColdStart);
        if (state.State == "S5")
        {
            if (marker.Length == 0)
            {
                marker = state.Claim.Length > 0 ? state.Claim : NoClaim;
                if (await services.Scripts.FieldsAsync(key, state.Id, [(StateFields.ColdStart, marker)], [], Fence(group)).ConfigureAwait(false) != MoveScripts.Written)
                {
                    return null;
                }
            }

            // Before the sweep stored lswept the claim field is still open and needs no reopen.
            if (marker != NoClaim && state.Swept == state.Id && await services.Claim.ReopenAsync(group, marker, Fence(group), cancellationToken).ConfigureAwait(false) != ClaimOutcome.Done)
            {
                Notify(group, "abandon_refused", "the move claim could not be reopened");
                return null;
            }

            var ep = state.Ep + 1;
            var target = new StateTarget("S0", "none", 0, ep, MoveScripts.NewNonce(), StateRecord.FormatPrev(state.State, state.Direction, state.NPop, state.Ep, state.Id), time.GetUtcNow().ToUnixTimeMilliseconds());
            return await services.Sequence.RunAsync(group, StateExpectation.Holding(state.Ep, state.Id, 1), target, new SequenceOptions(Ledger: services.Ledger, HoldsLease: () => services.HoldsLease(group), Fence: Fence(group)), cancellationToken).ConfigureAwait(false);
        }

        return await PurgeAfterFlipAsync(group, state, marker, input, cancellationToken).ConfigureAwait(false);
    }

    // Every fresh advert acknowledged the S0 identity and recycled its connections after it; then N and L are purged, and the claim steps follow.
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
        await services.Data.PurgeLegacyAsync(group, guard, cancellationToken).ConfigureAwait(false);
        var key = services.Names.State(group);
        if (marker != NoClaim)
        {
            if (await services.Claim.CertifyAsync(group, marker, state.Ep, "S0", Fence(group), cancellationToken).ConfigureAwait(false) == ClaimOutcome.Failed)
            {
                Notify(group, "claim_release_failed", marker);
                return null;
            }

            await services.Scripts.FieldsAsync(key, state.Id, [(StateFields.Release, $"{marker}:{state.Ep}:S0")], [], Fence(group)).ConfigureAwait(false);
            if (await services.Claim.ReleaseIdleAsync(group, marker, Fence(group), cancellationToken).ConfigureAwait(false) == ClaimOutcome.Failed)
            {
                Notify(group, "claim_release_failed", marker);
                return null;
            }
        }

        await services.Scripts.FieldsAsync(key, state.Id, [], [StateFields.ColdStart], Fence(group)).ConfigureAwait(false);
        return new TransitionResult(TransitionOutcome.Completed);
    }
}
