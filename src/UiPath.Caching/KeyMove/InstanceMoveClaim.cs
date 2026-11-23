using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

// Confirm the Dst mirror before sending Src PEND so a lost reply on Src still leaves evidence of the acquisition.
internal sealed class InstanceMoveClaim(ClaimStore src, ClaimStore dstMirror, IGenerationSource generations, Func<string, bool> holdsLease, Action<string, string, string>? alert, IAcquisitionTokens? tokens = null) : IMoveClaim
{
    public const string Descriptor = "inst";

    private const string ClaimConflict = "claim_conflict";
    private const string AuthorityKind = "im:";
    private const string MirrorKind = "imd:";

    // The Dst generation at the last confirmed mirror OPEN of each (group, token).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Group, string Token), ServerGeneration> _confirmed = new();

    private enum SrcPend
    {
        Applied,
        NotApplied,
        Uncertain,
    }

    public static ClaimOutcome Map(ClaimResult result) => result.Code switch
    {
        ClaimCodes.Done or ClaimCodes.AlreadyDone => result.Durability switch
        {
            ClaimDurability.Confirmed or ClaimDurability.NoReplica => ClaimOutcome.Done,
            ClaimDurability.NotConfirmed => ClaimOutcome.DoneUnconfirmed,
            _ => ClaimOutcome.Failed,
        },
        ClaimCodes.Refused or ClaimCodes.OtherGroup or ClaimCodes.OtherMove or ClaimCodes.TokenSpent => ClaimOutcome.Refused,
        _ => ClaimOutcome.Failed,
    };

    public async Task<string?> AcquireTokenAsync(string group, LeaseFence? fence, CancellationToken cancellationToken)
    {
        if (tokens is null)
        {
            return MoveScripts.NewNonce();
        }

        if (LeaseLost(group))
        {
            return null;
        }

        try
        {
            if (await tokens.ReadAsync(group, cancellationToken).ConfigureAwait(false) is { } stored)
            {
                return stored.Token;
            }

            var fresh = MoveScripts.NewNonce();
            return await tokens.WriteAsync(group, fresh, fence, cancellationToken).ConfigureAwait(false) ? fresh : null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return null;
        }
    }

    public Task<TransitionResult?> CommitGuardAsync(string group, string token, ConnectionProof? marker, CancellationToken cancellationToken)
    {
        if (_confirmed.TryGetValue((group, token), out var held) && marker is { } proof && held.Includes(proof.RunId, proof.ReplicationId))
        {
            return Task.FromResult<TransitionResult?>(null);
        }

        alert?.Invoke(group, KeyMoveGateIds.MirrorRepairPending, "the commit marker was not written in the Dst generation the mirror was confirmed in");
        return Task.FromResult<TransitionResult?>(new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "the Dst generation of the commit marker is not the one the mirror was confirmed in"));
    }

    public async Task<ClaimOutcome> PendAsync(string group, string token, string descriptor, LeaseFence? fence, CancellationToken cancellationToken)
    {
        if (LeaseLost(group))
        {
            return ClaimOutcome.Failed;
        }

        var mirror = await ConfirmMirrorPendAsync(group, token, fence, cancellationToken).ConfigureAwait(false);
        if (mirror != ClaimOutcome.Done)
        {
            return mirror;
        }

        // Store the uncertainty mark before Src PEND so a retry can account for a write whose reply was lost.
        if (tokens is not null && !await tokens.MarkSrcUncertainAsync(group, fence, cancellationToken).ConfigureAwait(false))
        {
            return PendUnavailable(group, "the mark that a Src pend may exist could not be stored");
        }

        var (outcome, applied) = await SrcPendAsync(group, token, cancellationToken).ConfigureAwait(false);
        if (outcome == ClaimOutcome.Done)
        {
            return outcome;
        }

        return await ReconcilePendAsync(group, token, fence, outcome, applied, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClaimOutcome> OpenAsync(string group, string token, bool restore, LeaseFence? fence, CancellationToken cancellationToken)
    {
        if (LeaseLost(group))
        {
            return ClaimOutcome.Failed;
        }

        ClaimOutcome outcome;
        try
        {
            outcome = Map(await src.OpenAsync(AuthorityKind + group, token, Descriptor, restore).ConfigureAwait(false));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ClaimOutcome.Failed;
        }

        if (outcome is not (ClaimOutcome.Done or ClaimOutcome.DoneUnconfirmed))
        {
            return outcome;
        }

        // Nothing past this point may skip: a mirror that is not confirmed holds the commit, even where the transition is past its marker.
        return await MirrorOpenAsync(group, token, cancellationToken).ConfigureAwait(false) ? outcome : ClaimOutcome.DoneUnconfirmed;
    }

    public Task<ClaimOutcome> ReopenAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) => Task.FromResult(ClaimOutcome.Failed);

    public Task<ClaimOutcome> CertifyAsync(string group, string token, long ep, string terminal, LeaseFence? fence, CancellationToken cancellationToken)
    {
        var swept = terminal == "S5";
        return StepAsync(group, () => src.CertifyAsync(AuthorityKind + group, token, ep, swept), () => dstMirror.CertifyAsync(MirrorKind + group, token, ep, swept), cancellationToken);
    }

    public Task<ClaimOutcome> ReleaseIdleAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) =>
        Settled(group, token, StepAsync(group, () => src.IdleAsync(AuthorityKind + group, token), () => dstMirror.IdleAsync(MirrorKind + group, token), cancellationToken));

    public Task<ClaimOutcome> CloseAsync(string group, string token, CancellationToken cancellationToken) =>
        Settled(group, token, StepAsync(group, () => src.CloseAsync(AuthorityKind + group, token), () => dstMirror.CloseAsync(MirrorKind + group, token), cancellationToken));

    public async Task<ClaimStatus> StatusAsync(string group, string token, CancellationToken cancellationToken)
    {
        var record = await src.ReadAsync().ConfigureAwait(false);
        if (!record.Moves.TryGetValue(AuthorityKind + group, out var value) || value.Token != token)
        {
            return ClaimStatus.Absent;
        }

        return value.Status switch
        {
            ClaimGrammar.Pending => ClaimStatus.Pending,
            ClaimGrammar.Open => ClaimStatus.Open,
            _ => ClaimStatus.Absent,
        };
    }

    // The generation held for a move is dropped once the move ends: a later move of the group has its own OPEN.
    private async Task<ClaimOutcome> Settled(string group, string token, Task<ClaimOutcome> step)
    {
        var outcome = await step.ConfigureAwait(false);
        if (outcome is ClaimOutcome.Done or ClaimOutcome.DoneUnconfirmed)
        {
            _confirmed.TryRemove((group, token), out _);
        }

        return outcome;
    }

    private async Task<ClaimOutcome> ConfirmMirrorPendAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken)
    {
        try
        {
            var mirror = await dstMirror.PendConfirmedAsync(MirrorKind + group, token, Descriptor).ConfigureAwait(false);
            switch (Map(mirror))
            {
                case ClaimOutcome.Refused:
                    alert?.Invoke(group, ClaimConflict, $"the Dst claim record refused the mirror pend ({mirror.Code})");
                    if (mirror.Code == ClaimCodes.TokenSpent)
                    {
                        await ForgetTokenAsync(group, fence, cancellationToken).ConfigureAwait(false);
                    }

                    return ClaimOutcome.Refused;
                case ClaimOutcome.Done:
                    return ClaimOutcome.Done;
                case ClaimOutcome.DoneUnconfirmed:
                    return PendUnavailable(group, "the Dst mirror pend is not confirmed on a replica");
                default:
                    return ClaimOutcome.Failed;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return PendUnavailable(group, $"the Dst mirror pend could not be confirmed: {ex.GetType().Name}");
        }
    }

    private async Task<ClaimOutcome> ReconcilePendAsync(string group, string token, LeaseFence? fence, ClaimOutcome outcome, SrcPend applied, CancellationToken cancellationToken)
    {
        // Every cleanup of the mirror reconciles with the Src first: a live pend of this token keeps it (and counts as pended), an unreadable Src keeps it, none found may release it.
        switch (await SrcHoldsPendAsync(group, token, cancellationToken).ConfigureAwait(false))
        {
            case true:
                return ClaimOutcome.Done;
            case null:
                alert?.Invoke(group, "claim_pend_uncertain", "the Src pend cannot be read back; the Dst mirror pend was kept");
                return outcome;
        }

        // A command whose reply was lost may still land after this read shows nothing: that attempt keeps the mirror, and the next one reads the Src again.
        if (applied == SrcPend.Uncertain)
        {
            alert?.Invoke(group, "claim_pend_uncertain", "the Src pend may still apply; the Dst mirror pend was kept");
            return outcome;
        }

        // The token is dropped only once the mirror is known to hold no live pend of it: otherwise the survivor would refuse every later token.
        if (await ReleaseMirrorPendAsync(group, token, cancellationToken).ConfigureAwait(false))
        {
            await ForgetTokenAsync(group, fence, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    private ClaimOutcome PendUnavailable(string group, string detail)
    {
        alert?.Invoke(group, KeyMoveGateIds.PendWaitUnavailable, $"{detail}; the Src claim was not started");
        return ClaimOutcome.Failed;
    }

    private async Task<(ClaimOutcome Outcome, SrcPend Applied)> SrcPendAsync(string group, string token, CancellationToken cancellationToken)
    {
        try
        {
            return await src.PendAsync(AuthorityKind + group, token, Descriptor, string.Empty).ConfigureAwait(false) switch
            {
                ClaimCodes.Done or ClaimCodes.AlreadyDone => (ClaimOutcome.Done, SrcPend.Applied),
                ClaimCodes.Refused or ClaimCodes.OtherGroup or ClaimCodes.OtherMove or ClaimCodes.TokenSpent => (ClaimOutcome.Refused, SrcPend.NotApplied),
                ClaimCodes.BadArgument or ClaimCodes.OwnMalformed or ClaimCodes.PointerChanged => (ClaimOutcome.Failed, SrcPend.NotApplied),
                _ => (ClaimOutcome.Failed, SrcPend.Uncertain),
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (ClaimOutcome.Failed, ServerBarrier.NotSent(ex) ? SrcPend.NotApplied : SrcPend.Uncertain);
        }
    }

    // A released token is spent (a pend of the same token is refused as TokenSpent), so the next acquisition must take a new one.
    private async Task ForgetTokenAsync(string group, LeaseFence? fence, CancellationToken cancellationToken)
    {
        if (tokens is not null)
        {
            await tokens.ClearAsync(group, fence, cancellationToken).ConfigureAwait(false);
        }
    }

    // Whether the Src shows a live pend or open of this token; null when it cannot be read.
    private async Task<bool?> SrcHoldsPendAsync(string group, string token, CancellationToken cancellationToken)
    {
        try
        {
            var record = await src.ReadAsync().ConfigureAwait(false);
            return record.Moves.TryGetValue(AuthorityKind + group, out var value) && value.Token == token && value.IsLive;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // True when the mirror holds no live pend of this token afterwards: the release was applied and confirmed, or a read of the Dst shows the field idle (or gone, or another move's).
    private async Task<bool> ReleaseMirrorPendAsync(string group, string token, CancellationToken cancellationToken)
    {
        try
        {
            if (Map(await dstMirror.IdleAsync(MirrorKind + group, token).ConfigureAwait(false)) == ClaimOutcome.Done)
            {
                return true;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The reply may be lost after the release applied: the read below says which.
        }

        try
        {
            var record = await dstMirror.ReadAsync().ConfigureAwait(false);
            return !(record.Moves.TryGetValue(MirrorKind + group, out var value) && value.Token == token && value.IsLive);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // Pin the confirmed mirror write and read-back to one Dst generation.
    private async Task<bool> MirrorOpenAsync(string group, string token, CancellationToken cancellationToken)
    {
        var held = _confirmed.TryGetValue((group, token), out var recorded) ? recorded : (ServerGeneration?)null;
        var before = await generations.ReadAsync(RecordSide.Dst, cancellationToken).ConfigureAwait(false);
        var repair = held is not null && before != held;
        if (before is not { } generation)
        {
            return MirrorBlocked(group, repair, "the Dst generation cannot be read");
        }

        if (!await WriteAndReadMirrorAsync(group, token, repair, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (await generations.ReadAsync(RecordSide.Dst, cancellationToken).ConfigureAwait(false) != generation)
        {
            return MirrorBlocked(group, repair, "the Dst generation changed while the mirror was opened");
        }

        _confirmed[(group, token)] = generation;
        return true;
    }

    private async Task<bool> WriteAndReadMirrorAsync(string group, string token, bool repair, CancellationToken cancellationToken)
    {
        try
        {
            var result = await dstMirror.OpenAsync(MirrorKind + group, token, Descriptor, restore: true).ConfigureAwait(false);
            if (Map(result) == ClaimOutcome.Refused)
            {
                alert?.Invoke(group, ClaimConflict, $"the Dst claim record refused the mirror open ({result.Code})");
                return false;
            }

            if (Map(result) != ClaimOutcome.Done)
            {
                return MirrorBlocked(group, repair, "the Dst mirror open is not confirmed on a replica");
            }

            var record = await dstMirror.ReadAsync().ConfigureAwait(false);
            if (!record.Moves.TryGetValue(MirrorKind + group, out var value) || value.Token != token || value.Status != ClaimGrammar.Open)
            {
                return MirrorBlocked(group, repair, "the Dst does not show the mirror open under this token");
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return MirrorBlocked(group, repair, "the Dst mirror could not be written or read");
        }

        return true;
    }

    private bool MirrorBlocked(string group, bool repair, string detail)
    {
        alert?.Invoke(group, repair ? KeyMoveGateIds.MirrorRepairPending : KeyMoveGateIds.MirrorOpenUnavailable, detail);
        return false;
    }

    private bool LeaseLost(string group)
    {
        if (holdsLease(group))
        {
            return false;
        }

        alert?.Invoke(group, "lease_lost", "the claim step was not sent");
        return true;
    }

    private async Task<ClaimOutcome> StepAsync(string group, Func<Task<ClaimResult>> authority, Func<Task<ClaimResult>> mirror, CancellationToken cancellationToken)
    {
        if (LeaseLost(group))
        {
            return ClaimOutcome.Failed;
        }

        ClaimOutcome outcome;
        try
        {
            outcome = Map(await authority().ConfigureAwait(false));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ClaimOutcome.Failed;
        }

        if (outcome is ClaimOutcome.Done or ClaimOutcome.DoneUnconfirmed)
        {
            await MirrorAsync(group, mirror, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    // The mirror never gates the Src: a conflict is raised, anything else short of a confirmed step is a skipped wait on the Dst.
    private async Task MirrorAsync(string group, Func<Task<ClaimResult>> mirror, CancellationToken cancellationToken)
    {
        try
        {
            var result = await mirror().ConfigureAwait(false);
            if (result.Code is ClaimCodes.OtherGroup or ClaimCodes.OtherMove)
            {
                alert?.Invoke(group, ClaimConflict, $"the Dst claim record refused the mirror ({result.Code})");
            }
            else if (Map(result) != ClaimOutcome.Done)
            {
                alert?.Invoke(group, "wait_skipped", "claim,dst");
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            alert?.Invoke(group, "wait_skipped", "claim,dst");
        }
    }
}
