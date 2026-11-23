namespace UiPath.Caching.KeyMove;

// Confirm the baselines before the pend. Advance a baseline only after proof in the new generation.
internal sealed class AdmissionRecordingClaim(
    InstanceMoveClaim inner,
    string app,
    IGenerationSource generations,
    IAdmissionBaselines baselines,
    Func<CancellationToken, Task<ClaimRecord>> readSrcClaim,
    Action<string, string, string>? alert = null,
    IAcquisitionGuard? guard = null) : IInstanceClaimSteps
{
    // The default members of IMoveClaim are forwarded, not inherited: a decorator that left them out would give every acquisition a new token that is stored nowhere and let a marker pass unguarded.
    public Task<string?> AcquireTokenAsync(string group, LeaseFence? fence, CancellationToken cancellationToken) => inner.AcquireTokenAsync(group, fence, cancellationToken);

    public Task<TransitionResult?> CommitGuardAsync(string group, string token, ConnectionProof? marker, CancellationToken cancellationToken) => inner.CommitGuardAsync(group, token, marker, cancellationToken);

    public async Task<ClaimOutcome> PendAsync(string group, string token, string descriptor, LeaseFence? fence, CancellationToken cancellationToken)
    {
        // Both baselines are written and confirmed before anything is pended, in the order of the pend mark: a Src pend that applies and is lost, or a mirror that is lost, then always finds a baseline of this
        // acquisition. An existing baseline of this token is kept, so a retry never moves a baseline to a generation whose mirror has not been repaired.
        if (await EnsureBaselinesAsync(group, token, fence, cancellationToken).ConfigureAwait(false) is not { } generation)
        {
            return ClaimOutcome.Failed;
        }

        var outcome = await inner.PendAsync(group, token, descriptor, fence, cancellationToken).ConfigureAwait(false);
        if (outcome != ClaimOutcome.Done)
        {
            return outcome;
        }

        // The mirror PEND was confirmed: the Dst baseline advances to a new generation only if the Dst held that generation across the whole step.
        if (!await AdvanceDstAsync(group, token, generation, fence, cancellationToken).ConfigureAwait(false))
        {
            return Unavailable(group, "the Dst baseline could not follow the confirmed mirror pend");
        }

        return await WriteSrcAsync(group, token, ClaimGrammar.Pending, fence, cancellationToken).ConfigureAwait(false) ? outcome : ClaimOutcome.Failed;
    }

    public async Task<ClaimOutcome> OpenAsync(string group, string token, bool restore, LeaseFence? fence, CancellationToken cancellationToken)
    {
        var before = await generations.ReadAsync(RecordSide.Dst, cancellationToken).ConfigureAwait(false);
        var outcome = await inner.OpenAsync(group, token, restore, fence, cancellationToken).ConfigureAwait(false);
        if (outcome != ClaimOutcome.Done)
        {
            return outcome;
        }

        // The mirror was confirmed in a generation that held across the step; the baseline follows it only when this read agrees.
        var after = await generations.ReadAsync(RecordSide.Dst, cancellationToken).ConfigureAwait(false);
        if (before is null || before != after || !await WriteDstAsync(group, token, after.Value, fence, cancellationToken).ConfigureAwait(false))
        {
            return Unavailable(group, "the Dst baseline could not follow the confirmed mirror open");
        }

        return await WriteSrcAsync(group, token, ClaimGrammar.Open, fence, cancellationToken).ConfigureAwait(false) ? outcome : ClaimOutcome.DoneUnconfirmed;
    }

    public Task<ClaimOutcome> ReopenAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) => inner.ReopenAsync(group, token, fence, cancellationToken);

    public Task<ClaimOutcome> CertifyAsync(string group, string token, long ep, string terminal, LeaseFence? fence, CancellationToken cancellationToken) =>
        inner.CertifyAsync(group, token, ep, terminal, fence, cancellationToken);

    public Task<ClaimOutcome> ReleaseIdleAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) =>
        EndedAsync(group, token, inner.ReleaseIdleAsync(group, token, fence, cancellationToken), cancellationToken);

    public Task<ClaimOutcome> CloseAsync(string group, string token, CancellationToken cancellationToken) =>
        EndedAsync(group, token, inner.CloseAsync(group, token, cancellationToken), cancellationToken);

    public Task<ClaimStatus> StatusAsync(string group, string token, CancellationToken cancellationToken) => inner.StatusAsync(group, token, cancellationToken);

    // The move has ended: nothing is left for a gate to wait for. A baseline that cannot be removed is removed by the claim recovery of a settled field.
    private async Task<ClaimOutcome> EndedAsync(string group, string token, Task<ClaimOutcome> step, CancellationToken cancellationToken)
    {
        var outcome = await step.ConfigureAwait(false);
        if (outcome is ClaimOutcome.Done or ClaimOutcome.DoneUnconfirmed)
        {
            try
            {
                await baselines.ClearAsync(app, token, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException)
            {
                alert?.Invoke(group, KeyMoveGateIds.BaselineUnavailable, $"the baselines could not be removed: {ex.GetType().Name}");
            }
        }

        return outcome;
    }

    private ClaimOutcome Unavailable(string group, string detail)
    {
        alert?.Invoke(group, KeyMoveGateIds.BaselineUnavailable, detail);
        return ClaimOutcome.DoneUnconfirmed;
    }

    // The Dst generation the pend starts in, or null when a baseline could not be established. A baseline of another token is a leftover of an earlier move and is replaced by the acquisition that owns the
    // token; one of this token is kept.
    private async Task<ServerGeneration?> EnsureBaselinesAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken)
    {
        try
        {
            var dst = await generations.ReadAsync(RecordSide.Dst, cancellationToken).ConfigureAwait(false);
            var src = await generations.ReadAsync(RecordSide.Src, cancellationToken).ConfigureAwait(false);
            if (dst is null || src is null)
            {
                alert?.Invoke(group, KeyMoveGateIds.BaselineUnavailable, "a generation cannot be read, so no baseline can be established before the pend");
                return null;
            }

            var heldDst = await baselines.ReadDstAsync(app, cancellationToken).ConfigureAwait(false);
            if (heldDst?.Token != token
                && !await RecordAsync(group, token, heldDst, new AdmissionBaseline(dst.Value, token), baselines.WriteDstAsync, fence, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var heldSrc = await baselines.ReadSrcAsync(app, cancellationToken).ConfigureAwait(false);
            if (heldSrc?.Token != token
                && !await RecordAsync(group, token, heldSrc, new AdmissionBaseline(src.Value, token), baselines.WriteSrcAsync, fence, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            // Confirmed: both are read back before the pend goes.
            if ((await baselines.ReadDstAsync(app, cancellationToken).ConfigureAwait(false))?.Token != token || (await baselines.ReadSrcAsync(app, cancellationToken).ConfigureAwait(false))?.Token != token)
            {
                alert?.Invoke(group, KeyMoveGateIds.BaselineUnavailable, "the baselines were not confirmed before the pend");
                return null;
            }

            return dst;
        }
        catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException or FormatException)
        {
            alert?.Invoke(group, KeyMoveGateIds.BaselineUnavailable, $"the baselines could not be established: {ex.GetType().Name}");
            return null;
        }
    }

    // After a confirmed mirror step that began in generation `began`: a baseline in another generation advances only if the Dst is still in `began`.
    private async Task<bool> AdvanceDstAsync(string group, string token, ServerGeneration began, LeaseFence? fence, CancellationToken cancellationToken)
    {
        try
        {
            var held = await baselines.ReadDstAsync(app, cancellationToken).ConfigureAwait(false);
            if (held is null || held.Token != token || held.Generation == began)
            {
                return held?.Token == token;
            }

            if (await generations.ReadAsync(RecordSide.Dst, cancellationToken).ConfigureAwait(false) != began)
            {
                return false;
            }

            return await RecordAsync(group, token, held, new AdmissionBaseline(began, token), baselines.WriteDstAsync, fence, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException or FormatException)
        {
            return false;
        }
    }

    // True when written (or already there); false and an alert when it could not be.
    private async Task<bool> WriteDstAsync(string group, string token, ServerGeneration generation, LeaseFence? fence, CancellationToken cancellationToken)
    {
        try
        {
            var held = await baselines.ReadDstAsync(app, cancellationToken).ConfigureAwait(false);
            return await RecordAsync(group, token, held, new AdmissionBaseline(generation, token), baselines.WriteDstAsync, fence, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException or FormatException)
        {
            alert?.Invoke(group, KeyMoveGateIds.BaselineUnavailable, $"the Dst baseline could not be written: {ex.GetType().Name}");
            return false;
        }
    }

    // Records the Src generation only after a confirmed read of a present claim of this token, between two equal reads of that generation.
    private async Task<bool> WriteSrcAsync(string group, string token, string status, LeaseFence? fence, CancellationToken cancellationToken)
    {
        try
        {
            var before = await generations.ReadAsync(RecordSide.Src, cancellationToken).ConfigureAwait(false);
            var record = await readSrcClaim(cancellationToken).ConfigureAwait(false);
            var after = await generations.ReadAsync(RecordSide.Src, cancellationToken).ConfigureAwait(false);
            var present = record.Moves.TryGetValue("im:" + group, out var value) && value.Token == token && value.Status == status;
            if (before is not { } generation || before != after || !present)
            {
                alert?.Invoke(group, KeyMoveGateIds.BaselineUnavailable, "the Src claim read is not confirmed present in one generation");
                return false;
            }

            var held = await baselines.ReadSrcAsync(app, cancellationToken).ConfigureAwait(false);
            return await RecordAsync(group, token, held, new AdmissionBaseline(generation, token), baselines.WriteSrcAsync, fence, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StackExchange.Redis.RedisException or TimeoutException or FormatException)
        {
            alert?.Invoke(group, KeyMoveGateIds.BaselineUnavailable, $"the Src baseline could not be written: {ex.GetType().Name}");
            return false;
        }
    }

    // The one way a baseline is written. `held` is what the caller read, before this check; the write compares against exactly that, so a newer acquisition that wrote since makes it fail.
    private async Task<bool> RecordAsync(
        string group,
        string token,
        AdmissionBaseline? held,
        AdmissionBaseline value,
        Func<string, AdmissionBaseline, AdmissionBaseline?, CancellationToken, Task<bool>> write,
        LeaseFence? fence,
        CancellationToken cancellationToken)
    {
        if (held == value)
        {
            return true;
        }

        var hold = guard is null ? AcquisitionHold.Owned : await guard.HoldsAsync(group, token, fence, cancellationToken).ConfigureAwait(false);
        if (hold is AcquisitionHold.LeaseNotHeld or AcquisitionHold.Other || (hold == AcquisitionHold.None && held is not null && held.Token != token))
        {
            alert?.Invoke(group, KeyMoveGateIds.BaselineUnavailable, $"the baseline was not written: the acquisition no longer owns it ({hold})");
            return false;
        }

        if (!await write(app, value, held, cancellationToken).ConfigureAwait(false))
        {
            alert?.Invoke(group, KeyMoveGateIds.BaselineUnavailable, "the baseline was not written: it changed since it was read");
            return false;
        }

        return true;
    }
}
