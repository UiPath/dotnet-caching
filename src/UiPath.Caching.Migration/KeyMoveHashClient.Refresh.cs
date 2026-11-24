using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveHashClient<T>
{
    /// <summary>The refresh that sets the expiry only, as the library's expiry overloads do: the metadata the hash holds stays.</summary>
    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken) =>
        RefreshCoreAsync(key, expiry, null, withOptions: false, cancellationToken);

    /// <summary>The refresh with options, as the library's options overload does: null metadata clears the metadata the hash holds, and others replace it.</summary>
    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, IDictionary<string, string?>? metadata, CancellationToken cancellationToken) =>
        RefreshCoreAsync(key, expiry, metadata, withOptions: true, cancellationToken);

    // 1 refreshed, 0 the hash is not there, -1 the identity changed.
    private static (WriteResult Result, bool Again) Judged(long code) => code switch
    {
        MoveScripts.IdentityMismatch => (default, true),
        0 => (WriteOutcome.NotSent, false),
        _ => (WriteOutcome.Committed, false),
    };

    private static HashMetadataClear MetadataClear(bool clearing, bool cachesNulls)
    {
        if (!clearing)
        {
            return HashMetadataClear.Keep;
        }

        return cachesNulls ? HashMetadataClear.Mark : HashMetadataClear.Remove;
    }

    private async ValueTask<WriteResult> RefreshCoreAsync(string key, CacheExpiry expiry, IDictionary<string, string?>? metadata, bool withOptions, CancellationToken cancellationToken)
    {
        var plan = PlanFor(write: true);
        if (plan.Mode == KeyMoveMode.Plain)
        {
            return await PlainRefreshAsync(key, expiry, metadata, withOptions, cancellationToken).ConfigureAwait(false);
        }

        // The metadata field has no checked form until the keys have moved.
        if (metadata is not null)
        {
            ThrowIfRefreshMetadataRefused(plan.Mode);
        }

        var at = expiry.Deadline ?? (expiry.Relative is { } span ? _p.Clock.GetUtcNow() + span : throw new ArgumentException("The router resolves the default life before a refresh.", nameof(expiry)));

        // A life that is a count below 1 ms is a delete.
        if ((long)Math.Ceiling((at - _p.Clock.GetUtcNow()).TotalMilliseconds) < 1)
        {
            return await RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }

        return await RefreshManagedAsync(key, at.ToUnixTimeMilliseconds(), metadata, withOptions, plan, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<WriteResult> RefreshManagedAsync(string key, long at, IDictionary<string, string?>? metadata, bool withOptions, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        var run = -1;
        while (true)
        {
            run++;
            plan = await SettleWaitAsync(plan, cancellationToken).ConfigureAwait(false);
            if (plan.Mode == KeyMoveMode.Plain)
            {
                return await PlainRefreshAsync(key, CacheExpiry.At(DateTimeOffset.FromUnixTimeMilliseconds(at)), metadata, withOptions, cancellationToken).ConfigureAwait(false);
            }

            ThrowIfNoManagedWrite(plan);
            var (admitted, replan) = Admit(promotion: false, plan.Act?.Id);
            if (replan)
            {
                plan = await ReplanAfterAdmissionAsync(run, cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var unit = admitted ?? throw Unavailable();
            var (result, again) = await RefreshOnceAsync(key, at, metadata, withOptions, plan, unit, cancellationToken).ConfigureAwait(false);
            if (!again)
            {
                return result;
            }

            // The identity changed, or the key moved: the refresh runs again under the rules in force.
            Count("identity_rerun");
            if (run >= _options.MaxReruns)
            {
                return WriteOutcome.NotSent;
            }

            plan = PlanFor(write: true);
        }
    }

    private async ValueTask<(WriteResult Result, bool Again)> RefreshOnceAsync(string key, long at, IDictionary<string, string?>? metadata, bool withOptions, KeyMovePlan plan, AdmittedUnit unit, CancellationToken cancellationToken)
    {
        if (metadata is not null)
        {
            ThrowIfRefreshMetadataRefused(plan.Mode);
        }

        if (cancellationToken.IsCancellationRequested || Data is not { } data || !data.IsConnected())
        {
            return (WriteOutcome.NotSent, false);
        }

        var l = InPlace(key);
        var n = Native(key);
        var fence = new FenceBump(data.Names.ForwardFence(n), MoveScripts.NewNonce(), FenceTtlMs);

        // The options refresh with no metadata clears what the side holding the hash keeps, atomically with the expiry: where the library caches nulls it leaves the empty marker, as its own refresh does. An in-place foreign format has no metadata to clear.
        var clearing = withOptions && metadata is null;
        var clearOfN = MetadataClear(clearing, _p.Native.CachesNulls);
        var clearOfL = MetadataClear(clearing && _p.InPlace.CarriesMetadata, _p.InPlace.CachesNulls);
        try
        {
            switch (plan.Mode)
            {
                case KeyMoveMode.S1:
                case KeyMoveMode.S2:
                    return Judged(await ExpireAsync(data, l, unit, at, null, clearOfL).ConfigureAwait(false));
                case KeyMoveMode.S5 when withOptions:
                    // The keys have moved, so the hash is the library's own and the refresh is the library's options refresh, which sets or clears the metadata with the expiry on a hash that is there.
                    return (await PlainRefreshAsync(key, CacheExpiry.At(DateTimeOffset.FromUnixTimeMilliseconds(at)), metadata, withOptions, cancellationToken).ConfigureAwait(false), false);
                case KeyMoveMode.S5:
                    return Judged(await ExpireAsync(data, n, unit, at, null).ConfigureAwait(false));
                case KeyMoveMode.S4:
                    // A refresh is acknowledged only where it lands on N: L is no copy that survives for good.
                    return Judged(await ExpireAsync(data, n, unit, at, fence, clearOfN).ConfigureAwait(false));
                default:
                    return await RefreshForwardAsync(key, at, clearOfN, unit, data, fence, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The extension may have applied.
            return (Typed(WriteOutcome.Unknown), false);
        }
    }

    // S3: a refresh is acknowledged only when it lands on N. L is never extended: a field delete can make an oversized or corrupt L promotable, and a promoter that took its life before the extension would publish that earlier expiry and delete L, so no extension of L is safe and none is acknowledged.
    // N is refreshed, or the hash is promoted and N is asked again, after every promotion including the last. A hash that cannot be promoted (above the caps or unreadable), or whose promotion does not finish, is not sent.
    private async ValueTask<(WriteResult Result, bool Again)> RefreshForwardAsync(string key, long at, HashMetadataClear clear, AdmittedUnit unit, KeyMoveData data, FenceBump fence, CancellationToken cancellationToken)
    {
        var l = InPlace(key);
        var n = Native(key);
        for (var attempt = 0; attempt < MaxEnsureAttempts; attempt++)
        {
            var code = await ExpireAsync(data, n, unit, at, fence, clear).ConfigureAwait(false);
            if (code != 0)
            {
                return Judged(code);
            }

            var present = await _reads.ExistsAsync(l, SchedulePointStep.ProbeSource, cancellationToken).ConfigureAwait(false);
            if (present is null)
            {
                return (WriteOutcome.NotSent, false);
            }

            if (present == false)
            {
                // An L that is absent proves a promoter published N before, so N is asked once more.
                return Judged(await ExpireAsync(data, n, unit, at, fence, clear).ConfigureAwait(false));
            }

            var promoted = await _promotion.PromoteAsync(key, HashPromotionFlavour.Ensure, cancellationToken).ConfigureAwait(false);
            switch (promoted.Status)
            {
                case HashPromotionStatus.Replan:
                    return (default, true);
                case HashPromotionStatus.Unavailable:
                case HashPromotionStatus.TooLarge:
                case HashPromotionStatus.Unreadable:
                    return (WriteOutcome.NotSent, false);
            }
        }

        // The last promotion has had its turn: N is asked once more.
        return Judged(await ExpireAsync(data, n, unit, at, fence, clear).ConfigureAwait(false));
    }

    private ValueTask<WriteResult> PlainRefreshAsync(string key, CacheExpiry expiry, IDictionary<string, string?>? metadata, bool withOptions, CancellationToken cancellationToken) =>
        withOptions ? _p.Plain.RefreshAsync(key, expiry, metadata, cancellationToken) : _p.Plain.RefreshAsync(key, expiry, cancellationToken);

    private void ThrowIfRefreshMetadataRefused(KeyMoveMode mode)
    {
        if (mode is KeyMoveMode.S1 or KeyMoveMode.S2 or KeyMoveMode.S3 or KeyMoveMode.S4)
        {
            throw new NotSupportedException($"A refresh of the hash cache '{_p.CacheId}' that carries metadata cannot run before its keys have moved, because the metadata field has no checked form there.");
        }
    }

    private async ValueTask<long> ExpireAsync(KeyMoveData data, string side, AdmittedUnit unit, long at, FenceBump? fence, HashMetadataClear clear = HashMetadataClear.Keep)
    {
        await At(SchedulePointStep.HashExpire).ConfigureAwait(false);
        return await data.Scripts.HashExpireAsync(Key(side), ControlOf(data, side), unit.Id, at, fence, clear).ConfigureAwait(false);
    }
}
