using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The mover's side of one cache: the shared promotion run for a key found by scan, under its own admission unit, with the seam for a live supersession marker.</summary>
internal sealed class KeyMover<T> : IMoveTarget
    where T : class
{
    private readonly KeyMoveClientParts<T> _p;
    private readonly KeyPromotion<T> _promotion;
    private readonly KeyPromotion<T> _reversePromotion;
    private readonly ISupersessionHandler _supersession;

    public KeyMover(KeyMoveClientParts<T> parts, ISupersessionHandler? supersession = null)
    {
        _p = parts ?? throw new ArgumentNullException(nameof(parts));
        _supersession = supersession ?? SkipSupersession.Instance;
        var source = parts.Source ?? new InPlaceMoveSide<T>(parts.Plane, parts.Group, parts.InPlaceKey, parts.InPlace, parts.Mappable, parts.Clock);
        _promotion = new KeyPromotion<T>(parts, source);

        // The same side class reaches N with the native key and codec, the source of the reverse mover.
        var native = new InPlaceMoveSide<T>(parts.Plane, parts.Group, parts.NativeKey, parts.Native, parts.Mappable, parts.Clock);
        _reversePromotion = new KeyPromotion<T>(parts, native, reverse: true);
    }

    public string CacheId => _p.CacheId;

    public bool NoOrigin => _p.NoOrigin;

    internal ISupersessionHandler Supersession => _supersession;

    public ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, CancellationToken cancellationToken) =>
        // A NoOrigin cache is moved in full, whatever the minimum says.
        MoveWithAsync(_promotion, key, new MoverFlavor(_p.NoOrigin ? TimeSpan.Zero : minTtl, _supersession), identity, ownership, cancellationToken);

    public ValueTask<MoveResult> MoveBackAsync(string key, string identity, IMoveOwnership ownership, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return NoOrigin
            ? ValueTask.FromResult(MoveResult.Skip("no_reverse"))
            : MoveWithAsync(_reversePromotion, key, new MoverFlavor(TimeSpan.Zero, SkipSupersession.Instance), identity, ownership, cancellationToken);
    }

    public ValueTask<DryRunProbe> ProbeAsync(string key, TimeSpan minTtl, bool decode, CancellationToken cancellationToken) =>
        _promotion.ProbeAsync(key, new MoverFlavor(_p.NoOrigin ? TimeSpan.Zero : minTtl, _supersession), decode, cancellationToken);

    private static MoveResult ResultOf(PromotionResult<T> result) => result.Kind switch
    {
        PromotionKind.Promoted => MoveResult.Done with { Bytes = result.Bytes },
        PromotionKind.Drained => MoveResult.Drain with { Bytes = result.Bytes },
        PromotionKind.OwnershipLost => MoveResult.Skip(MoveResult.OwnershipLost),

        // The unit was moved to the identity now in force, which is not the runner's: the key and the batch end here.
        PromotionKind.IdentityAtCapture or PromotionKind.IdentityAtPublish => MoveResult.Skip("identity"),
        PromotionKind.SupersededRerun => MoveResult.Skip(result.Readmitted ? "identity" : "superseded"),
        PromotionKind.SupersededRetry => MoveResult.Skip("superseded"),
        _ => MoveResult.Skip(ReasonOf(result)),
    };

    private static string ReasonOf(PromotionResult<T> result) => result.Kind switch
    {
        PromotionKind.SourceAbsent => "duplicate",
        PromotionKind.Expired => "expired",
        PromotionKind.TooLarge => "too_large",
        PromotionKind.NullNotCached => "null_not_cached",
        PromotionKind.Unreadable => "unreadable",
        PromotionKind.Unmappable => "unmappable",
        PromotionKind.ShortTtl => "short_ttl",
        PromotionKind.Superseded => "superseded",
        PromotionKind.Unavailable => "unavailable",
        PromotionKind.GateClosed => "gate_closed",
        PromotionKind.FenceRefused => "fence_refused",
        PromotionKind.Expiring => "expiring",
        PromotionKind.Abandoned => result.Reason ?? "abandoned",
        _ => "refused",
    };

    private async ValueTask<MoveResult> MoveWithAsync(KeyPromotion<T> promotion, string key, MoverFlavor flavor, string identity, IMoveOwnership ownership, CancellationToken cancellationToken)
    {
        var attempt = 0;
        var retried = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (result, skipped) = await PromoteOnceAsync(promotion, key, flavor, identity, attempt, ownership, cancellationToken).ConfigureAwait(false);
            if (skipped is { } skip)
            {
                return skip;
            }

            if (result.Kind == PromotionKind.Restart)
            {
                attempt++;
            }
            else if (result.Kind == PromotionKind.SupersededRetry && !retried)
            {
                retried = true;
            }
            else
            {
                return ResultOf(result);
            }
        }
    }

    // One admitted run of the promotion; the skip is set when the key cannot be promoted under this runner's identity now.
    private async ValueTask<(PromotionResult<T> Result, MoveResult? Skipped)> PromoteOnceAsync(KeyPromotion<T> promotion, string key, MoverFlavor flavor, string identity, int attempt, IMoveOwnership ownership, CancellationToken cancellationToken)
    {
        using var unit = _p.Plane.Enter(_p.Group, promotion: true);
        if (unit is null)
        {
            return (default, MoveResult.Skip("not_admitted"));
        }

        // The runner holds one identity: a unit admitted under another is never used, so a state that moved on cannot be promoted under.
        if (unit.Id != identity)
        {
            return (default, MoveResult.Skip("identity"));
        }

        if (_p.Plane.Data is not { } data || !data.IsConnected())
        {
            return (default, MoveResult.Skip("unavailable"));
        }

        try
        {
            return (await promotion.RunAsync(key, flavor, unit, data, attempt, ownership, cancellationToken).ConfigureAwait(false), null);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("OOM ", StringComparison.Ordinal))
        {
            // The server refused the write for memory: the mover backs off for minutes.
            return (default, MoveResult.Skip("oom"));
        }
        catch (TimeoutException)
        {
            return (default, MoveResult.Skip("timeout"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            return (default, MoveResult.Skip("unavailable"));
        }
    }
}
