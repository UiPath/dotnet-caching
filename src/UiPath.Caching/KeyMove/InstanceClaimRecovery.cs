using System.Collections.Concurrent;
using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

// The Src field supplies mirror repair. A pending claim still owned by the acquisition token is left for that acquisition.
internal sealed class InstanceClaimRecovery(
    IInstanceClaimSteps claim,
    InstanceMirrorRepair mirror,
    InstanceClaimReader reader,
    KeyMoveStateOptions options,
    TimeProvider time,
    Action<string, string, string>? alert = null,
    IAdmissionBaselines? baselines = null,
    string app = "",
    IAcquisitionTokens? tokens = null,
    IGenerationSource? generations = null,
    IAcquisitionGuard? guard = null,
    Func<string, LeaseFence?>? fence = null)
{
    private const string ClaimOrphan = "claim_orphan";
    private readonly ConcurrentDictionary<(string Group, string Token), long> _pendSeen = new();

    public async Task RefreshAsync(string group, StateRecord? state, CancellationToken cancellationToken)
    {
        InstanceClaims claims;
        try
        {
            claims = await reader.ReadAsync(group, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            alert?.Invoke(group, "claim_unreadable", ex.GetType().Name);
            return;
        }

        if (claims.SrcMalformed)
        {
            alert?.Invoke(group, "claim_malformed", "the Src claim record holds a field outside the grammar");
            return;
        }

        if (claims.Src is null)
        {
            await RecreateAsync(group, state, claims, cancellationToken).ConfigureAwait(false);
            await ReleaseOrphanMirrorPendAsync(group, state, claims, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SettleAsync(group, claims.Src, state, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await RepairMirrorAsync(group, await reader.ReadAsync(group, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            await ReconcileOrphanBaselinesAsync(group, state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            alert?.Invoke(group, "claim_unreadable", ex.GetType().Name);
        }
    }

    private static bool Terminal(StateRecord state) => state.Durable && (state.State == "S0" || (state.State == "S5" && state.Swept == state.Id));

    private static (long Ep, bool Swept) ParseCert(string cert)
    {
        var colon = cert.IndexOf(':', StringComparison.Ordinal);
        return (long.Parse(cert[..colon], NumberStyles.None, CultureInfo.InvariantCulture), cert[(colon + 1)..] == "S5");
    }

    // The Src field is missing: a live group's field is recreated from ct; a settled group needs none.
    private async Task RecreateAsync(string group, StateRecord? state, InstanceClaims claims, CancellationToken cancellationToken)
    {
        if (state is null)
        {
            if (claims.Mirror is { IsLive: true })
            {
                alert?.Invoke(group, ClaimOrphan, "the mirror is live and neither the Src field nor the state exists");
            }

            return;
        }

        if (state.Claim.Length == 0 || !state.Durable || state.State == "S0")
        {
            return;
        }

        var token = state.Claim;
        if (await claim.OpenAsync(group, token, restore: true, null, cancellationToken).ConfigureAwait(false) is not ClaimOutcome.Done)
        {
            alert?.Invoke(group, "claim_restore_failed", "OPEN with restore from ct did not complete");
            return;
        }

        if (Terminal(state))
        {
            await CertifyFromStateAsync(group, token, state, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SettleAsync(string group, ClaimValue field, StateRecord? state, CancellationToken cancellationToken)
    {
        switch (field.Status)
        {
            case ClaimGrammar.Pending:
                await SettlePendAsync(group, field, state, cancellationToken).ConfigureAwait(false);
                break;
            case ClaimGrammar.Open when field.Cert.Length == 0:
                await SettleOpenAsync(group, field, state, cancellationToken).ConfigureAwait(false);
                break;
            case ClaimGrammar.Open:
                await ReleaseCertifiedAsync(group, field, state, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await ClearBaselinesAsync(group, field.Token, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task SettlePendAsync(string group, ClaimValue field, StateRecord? state, CancellationToken cancellationToken)
    {
        if (state?.Claim == field.Token || await AcquisitionOwnsAsync(group, field.Token, cancellationToken).ConfigureAwait(false) != false)
        {
            _pendSeen.TryRemove((group, field.Token), out _);
            return;
        }

        var now = time.GetTimestamp();
        var since = _pendSeen.GetOrAdd((group, field.Token), now);
        if (time.GetElapsedTime(since, now) >= options.ClaimPendHold && await claim.ReleaseIdleAsync(group, field.Token, null, cancellationToken).ConfigureAwait(false) is ClaimOutcome.Done or ClaimOutcome.DoneUnconfirmed)
        {
            _pendSeen.TryRemove((group, field.Token), out _);
        }
    }

    // Whether the stored acquisition holds this token: null when it cannot be read, which leaves everything as it is. Without a token seam nothing is owned.
    private async Task<bool?> AcquisitionOwnsAsync(string group, string token, CancellationToken cancellationToken)
    {
        if (tokens is null)
        {
            return false;
        }

        try
        {
            return await tokens.ReadAsync(group, cancellationToken).ConfigureAwait(false) is { } held && held.Token == token;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return null;
        }
    }

    // The Src field is absent (this refresh just read it): a mirror PEND with no acquisition behind it, or whose Src pend is marked uncertain and is now shown absent, is orphaned.
    private async Task ReleaseOrphanMirrorPendAsync(string group, StateRecord? state, InstanceClaims claims, CancellationToken cancellationToken)
    {
        if (claims.Mirror is not { Status: ClaimGrammar.Pending } pend || !claims.MirrorKnown || claims.MirrorMalformed || state?.Claim == pend.Token)
        {
            return;
        }

        AcquisitionToken? held = null;
        if (tokens is not null)
        {
            try
            {
                held = await tokens.ReadAsync(group, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                return;
            }
        }

        // A matching pt blocks the release whatever ptu says: the acquisition still owns this mirror, and a driver that is paused or retrying, or a Src PEND whose reply was lost, can still produce a live Src
        // pend that needs it. Only the acquisition itself, or an abandon, settles its own mirror; the hold cancels nothing.
        if (held?.Token == pend.Token)
        {
            return;
        }

        if (!HoldElapsed(group, pend.Token))
        {
            return;
        }

        if (await mirror.IdleAsync(group, pend.Token, cancellationToken).ConfigureAwait(false) is ClaimOutcome.Done or ClaimOutcome.DoneUnconfirmed)
        {
            _pendSeen.TryRemove((group, pend.Token), out _);
        }
    }

    private bool HoldElapsed(string group, string token)
    {
        var now = time.GetTimestamp();
        return time.GetElapsedTime(_pendSeen.GetOrAdd((group, token), now), now) >= options.ClaimPendHold;
    }

    private async Task SettleOpenAsync(string group, ClaimValue field, StateRecord? state, CancellationToken cancellationToken)
    {
        if (state is null)
        {
            alert?.Invoke(group, ClaimOrphan, "the claim is open with no certificate and the state is gone; the operator releases it");
            return;
        }

        if (state.Claim != field.Token)
        {
            alert?.Invoke(group, "claim_conflict", "the state names another claim token than the open field");
            return;
        }

        if (Terminal(state))
        {
            await CertifyFromStateAsync(group, field.Token, state, cancellationToken).ConfigureAwait(false);
        }
    }

    // REL cert from the state's terminal evidence, then idle (S0) or closed (S5 with lswept).
    private async Task CertifyFromStateAsync(string group, string token, StateRecord state, CancellationToken cancellationToken)
    {
        var swept = state.State == "S5";
        if (await claim.CertifyAsync(group, token, state.Ep, swept ? "S5" : "S0", null, cancellationToken).ConfigureAwait(false) is not (ClaimOutcome.Done or ClaimOutcome.DoneUnconfirmed))
        {
            return;
        }

        _ = swept
            ? await claim.CloseAsync(group, token, cancellationToken).ConfigureAwait(false)
            : await claim.ReleaseIdleAsync(group, token, null, cancellationToken).ConfigureAwait(false);
    }

    // The certificate is on the field: an S0 one goes to idle, an S5 one to closed only while the state shows S5.
    private async Task ReleaseCertifiedAsync(string group, ClaimValue field, StateRecord? state, CancellationToken cancellationToken)
    {
        var (certEp, swept) = ParseCert(field.Cert);
        if (!swept)
        {
            // A certificate can survive a failed DURABLE write. Release requires the matching published state.
            if (state is { Durable: true, State: "S0" } && state.Ep == certEp && state.Release == $"{field.Token}:{field.Cert}")
            {
                await claim.ReleaseIdleAsync(group, field.Token, null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                alert?.Invoke(group, "claim_cert_unpublished", "the claim carries an S0 certificate and the state does not show that identity as DURABLE with the matching rel; it is not released");
            }
        }
        else if (state is { State: "S5" })
        {
            await claim.CloseAsync(group, field.Token, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            alert?.Invoke(group, ClaimOrphan, "the claim carries an S5 certificate and the state does not show S5; it is not closed");
        }
    }

    // Only the baselines of the settled claim's own token go, and none while pt names a newer acquisition: that one owns its baselines and its mirror.
    private async Task ClearBaselinesAsync(string group, string token, CancellationToken cancellationToken)
    {
        if (baselines is null)
        {
            return;
        }

        try
        {
            if (tokens is not null && await tokens.ReadAsync(group, cancellationToken).ConfigureAwait(false) is { } held && held.Token != token)
            {
                return;
            }

            await baselines.ClearAsync(app, token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            alert?.Invoke(group, KeyMoveGateIds.BaselineUnavailable, $"the baselines could not be removed: {ex.GetType().Name}");
        }
    }

    // Src to mirror only: the mirror is brought to the status and certificate of the Src field, and a mirror that is ahead is left alone.
    private async Task RepairMirrorAsync(string group, InstanceClaims claims, CancellationToken cancellationToken)
    {
        if (claims.Src is not { } src || claims.SrcMalformed || !claims.MirrorKnown || src.Status == ClaimGrammar.Pending)
        {
            return;
        }

        if (claims.MirrorMalformed)
        {
            alert?.Invoke(group, "claim_malformed", "the Dst claim record holds a field outside the grammar");
            return;
        }

        var held = claims.Mirror is { } m && m.Token == src.Token ? m : null;
        if (claims.Mirror is { } other && other.Token != src.Token && other.IsLive)
        {
            alert?.Invoke(group, "claim_conflict", "the mirror holds another token than the Src field");
            return;
        }

        switch (src.Status)
        {
            case ClaimGrammar.Open:
                await RepairOpenAsync(group, src, held, cancellationToken).ConfigureAwait(false);
                break;
            case ClaimGrammar.Idle or ClaimGrammar.Closed:
                await RepairSettledAsync(group, src, held, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task RepairOpenAsync(string group, ClaimValue src, ClaimValue? held, CancellationToken cancellationToken)
    {
        if (held is { IsLive: false })
        {
            alert?.Invoke(group, "claim_mirror_ahead", $"the mirror is {held.Status} while the Src field is open; it is not copied to the Src");
            return;
        }

        if (held is not { Status: ClaimGrammar.Open } || await BaselineBehindAsync(src.Token, cancellationToken).ConfigureAwait(false))
        {
            // The confirmed mirror OPEN of the claim, with its generation check; it also repeats the Src OPEN, which is idempotent. A surviving OPEN mirror takes it too once the Dst generation moved past its baseline.
            await claim.OpenAsync(group, src.Token, restore: true, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (src.Cert.Length > 0 && held.Cert.Length == 0)
        {
            var (ep, swept) = ParseCert(src.Cert);
            await mirror.CertifyAsync(group, src.Token, ep, swept, cancellationToken).ConfigureAwait(false);
        }
    }

    // The baselines are read first, then the guard, then the claims: an acquisition writes pt before its baseline and its pend before it clears pt, so whatever the guard and the later read show of a token is
    // the truth about it by the time it is removed.
    private async Task ReconcileOrphanBaselinesAsync(string group, StateRecord? state, CancellationToken cancellationToken)
    {
        if (baselines is null || guard is null)
        {
            return;
        }

        try
        {
            var found = new[] { (await baselines.ReadDstAsync(app, cancellationToken).ConfigureAwait(false))?.Token, (await baselines.ReadSrcAsync(app, cancellationToken).ConfigureAwait(false))?.Token };
            foreach (var token in found.OfType<string>().Distinct())
            {
                if (state?.Claim == token)
                {
                    continue;
                }

                var hold = await guard.HoldsAsync(group, token, fence?.Invoke(group), cancellationToken).ConfigureAwait(false);
                if (hold is AcquisitionHold.LeaseNotHeld or AcquisitionHold.Other or AcquisitionHold.Owned)
                {
                    continue;
                }

                var fresh = await reader.ReadAsync(group, cancellationToken).ConfigureAwait(false);
                if (fresh.SrcMalformed || !fresh.MirrorKnown || fresh.MirrorMalformed || (fresh.Src is { IsLive: true } srcField && srcField.Token == token) || (fresh.Mirror is { IsLive: true } mirrorField && mirrorField.Token == token))
                {
                    continue;
                }

                await baselines.ClearAsync(app, token, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or FormatException)
        {
            alert?.Invoke(group, KeyMoveGateIds.BaselineUnavailable, $"the orphan baselines could not be reconciled: {ex.GetType().Name}");
        }
    }

    // Whether the Dst baseline of this token is of a generation other than the Dst now: the mirror was last confirmed before a promotion. Unreadable or unknown is not behind; nothing can be revalidated then.
    private async Task<bool> BaselineBehindAsync(string token, CancellationToken cancellationToken)
    {
        if (baselines is null || generations is null)
        {
            return false;
        }

        try
        {
            return await baselines.ReadDstAsync(app, cancellationToken).ConfigureAwait(false) is { } held
                && held.Token == token
                && await generations.ReadAsync(RecordSide.Dst, cancellationToken).ConfigureAwait(false) is { } current
                && current != held.Generation;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or FormatException)
        {
            return false;
        }
    }

    // A settled Src field: the mirror follows its certificate to idle or closed; a mirror that was never written needs nothing, but a lost closed one is recreated.
    private async Task RepairSettledAsync(string group, ClaimValue src, ClaimValue? held, CancellationToken cancellationToken)
    {
        var closed = src.Status == ClaimGrammar.Closed;
        var behind = held is null ? closed : held.IsLive;
        if (!behind || src.Cert.Length == 0)
        {
            return;
        }

        var (ep, swept) = ParseCert(src.Cert);
        // Closing requires an OPEN field first.
        if (held is not { Status: ClaimGrammar.Open } && (closed || held is null)
            && await mirror.RestoreOpenAsync(group, src.Token, cancellationToken).ConfigureAwait(false) is not ClaimOutcome.Done)
        {
            return;
        }

        if (held is not { Status: ClaimGrammar.Pending } && held?.Cert.Length is null or 0
            && await mirror.CertifyAsync(group, src.Token, ep, swept, cancellationToken).ConfigureAwait(false) is not (ClaimOutcome.Done or ClaimOutcome.DoneUnconfirmed))
        {
            return;
        }

        _ = closed
            ? await mirror.CloseAsync(group, src.Token, cancellationToken).ConfigureAwait(false)
            : await mirror.IdleAsync(group, src.Token, cancellationToken).ConfigureAwait(false);
        await ClearBaselinesAsync(group, src.Token, cancellationToken).ConfigureAwait(false);
    }
}
