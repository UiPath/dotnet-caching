using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>Promotes hash keys found by the mover using the cache's supersession handler.</summary>
internal sealed class HashMover<T> : IMoveTarget
    where T : class
{
    private const int MaxAttempts = 3;

    private readonly KeyMoveHashClientParts<T> _p;
    private readonly HashPromotion<T> _promotion;

    public HashMover(KeyMoveHashClientParts<T> parts, HashPromotion<T> promotion)
    {
        _p = parts ?? throw new ArgumentNullException(nameof(parts));
        _promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));
    }

    public string CacheId => _p.CacheId;

    public bool NoOrigin => _p.NoOrigin;

    public async ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, Action<long, long>? observeSourceTtl, CancellationToken cancellationToken)
    {
        var move = new HashPromotion<T>.MoveRequest(identity, NoOrigin ? TimeSpan.Zero : minTtl, ownership, observeSourceTtl);
        var result = await RunAsync(key, move, cancellationToken).ConfigureAwait(false);
        return result with { Bytes = move.Bytes };
    }

    public ValueTask<DryRunProbe> ProbeAsync(string key, TimeSpan minTtl, bool decode, CancellationToken cancellationToken) =>
        _promotion.ProbeAsync(key, NoOrigin ? TimeSpan.Zero : minTtl, decode, cancellationToken);

    private static MoveResult ResultOf(HashPromotionResult<T> result) => result.Status switch
    {
        HashPromotionStatus.Complete => result.Read.Kind == ReadOutcome.Found ? MoveResult.Done : MoveResult.Drain,
        HashPromotionStatus.TooLarge => MoveResult.Skip("too_large"),
        HashPromotionStatus.ShortTtl => MoveResult.Skip("short_ttl"),
        HashPromotionStatus.Unreadable => MoveResult.Skip("unreadable"),
        HashPromotionStatus.WrongDestinationType => MoveResult.Skip("destination_type"),
        HashPromotionStatus.NotPromoted => MoveResult.Skip("abandoned"),
        HashPromotionStatus.Replan => MoveResult.Skip("identity"),
        _ => MoveResult.Skip("unavailable"),
    };

    private async ValueTask<MoveResult> RunAsync(string key, HashPromotion<T>.MoveRequest move, CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await move.Ownership.HoldsAsync(cancellationToken).ConfigureAwait(false))
                {
                    return MoveResult.Skip(MoveResult.OwnershipLost);
                }

                if (_p.Plane.Data is not { } data || !data.IsConnected())
                {
                    return MoveResult.Skip("unavailable");
                }

                var promoted = await _promotion.PromoteForMoverAsync(key, move, cancellationToken).ConfigureAwait(false);
                if (promoted.Status != HashPromotionStatus.Changing)
                {
                    return ResultOf(promoted);
                }
            }

            return MoveResult.Skip("changing");
        }
        catch (OwnershipLostException)
        {
            return MoveResult.Skip(MoveResult.OwnershipLost);
        }
        catch (RedisServerException ex) when (HashPromotion<T>.IsMemoryRefusal(ex))
        {
            return MoveResult.Skip("oom");
        }
        catch (TimeoutException)
        {
            return MoveResult.Skip("timeout");
        }
    }
}
