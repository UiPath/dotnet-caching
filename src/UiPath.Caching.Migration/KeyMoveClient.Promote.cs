using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveClient<T>
{
    private static ReadStep<T> PromotionRead(PromotionResult<T> result) => result.Kind switch
    {
        PromotionKind.IdentityAtCapture => ReadStep<T>.Again,
        PromotionKind.IdentityAtPublish when result.Readmitted => ReadStep<T>.Again,
        PromotionKind.Restart => ReadStep<T>.Retry,
        _ => ReadStep<T>.Of(result.Read),
    };

    // Promotion runs after the router checks the local tier.
    private async ValueTask<ReadStep<T>> PromoteAsync(string key, KeyMovePlan plan, CancellationToken cancellationToken, bool probed = false)
    {
        try
        {
            var attempt = 0;
            while (true)
            {
                var step = await PromoteOnceAsync(key, plan, attempt, probed && attempt == 0, cancellationToken).ConfigureAwait(false);
                if (!step.Restart)
                {
                    return step;
                }

                attempt++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            return ReadStep<T>.Of(ReadOutcome.Unavailable);
        }
    }

    private async ValueTask<ReadStep<T>> PromoteOnceAsync(string key, KeyMovePlan plan, int attempt, bool probed, CancellationToken cancellationToken)
    {
        var n = Native(key);

        var identity = plan.Identity;

        // A batch read has already checked the destination and probed the source.
        if (!probed)
        {
            // Verify a replica miss on the master before consulting the hint.
            var replica = Interpret(await SnapAsync(n, replica: true, SchedulePointStep.ReadDestination, cancellationToken).ConfigureAwait(false), _p.Native);
            if (replica.Kind != ReadOutcome.Absent)
            {
                return ReadStep<T>.Of(replica);
            }

            var master = Interpret(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), _p.Native);
            if (master.Kind != ReadOutcome.Absent)
            {
                return ReadStep<T>.Of(master);
            }

            if (_hints.Has(key, identity))
            {
                return ReadStep<T>.Of(ReadOutcome.Absent);
            }

            // Probe before capture so a genuine miss creates no fence.
            var probe = await SourceExistsAsync(key, SchedulePointStep.ProbeSource, cancellationToken).ConfigureAwait(false);
            switch (probe)
            {
                case null:
                    return ReadStep<T>.Of(ReadOutcome.Unavailable);
                case false:
                    return ReadStep<T>.Of(await MissAsync(key, n, identity, cancellationToken).ConfigureAwait(false));
            }
        }

        var (admitted, _) = await AdmitAsync(promotion: true, plan.Act?.Id, cancellationToken).ConfigureAwait(false);
        using var unit = admitted;
        if (unit is null || Data is not { } data)
        {
            // If admission fails, confirm the destination again before serving a source value.
            return await UnpromotedAsync(key, cancellationToken).ConfigureAwait(false);
        }

        var result = await _promotion.RunAsync(key, null, unit, data, attempt, AlwaysOwned.Instance, cancellationToken).ConfigureAwait(false);
        switch (result.Kind)
        {
            case PromotionKind.IdentityAtCapture when !result.Readmitted:
                return await UnpromotedAsync(key, cancellationToken).ConfigureAwait(false);
            case PromotionKind.SourceAbsent or PromotionKind.SupersededRetry:
                // A superseded copy of L was deleted with its marker, so the key reads as N alone.
                return ReadStep<T>.Of(await MissAsync(key, n, identity, cancellationToken).ConfigureAwait(false));
            case PromotionKind.SupersededRerun:
                return result.Readmitted ? ReadStep<T>.Again : await UnpromotedAsync(key, cancellationToken).ConfigureAwait(false);
            case PromotionKind.Superseded:
                return ReadStep<T>.Of(ReadOutcome.Unavailable);
            default:
                return PromotionRead(result);
        }
    }

    private Publication PublicationFor(Capture capture, long px, long tcap) =>
        new(
            capture.Token ?? throw new InvalidOperationException("A captured fence requires a token."),
            capture.Gate ?? throw new InvalidOperationException("A captured fence requires a gate."),
            _p.CacheId,
            MoveScripts.NewNonce(),
            FenceTtlMs,
            px,
            tcap,
            true);

    // The non-promoting union read, N first and L, then N on the master again; what it finds is served and not kept locally, as an abandoned promotion's value is.
    private async ValueTask<ReadStep<T>> UnpromotedAsync(string key, CancellationToken cancellationToken)
    {
        var union = await ReadUnionAsync(key, withSource: true, cancellationToken).ConfigureAwait(false);
        return union.Read.Kind == ReadOutcome.Found ? ReadStep<T>.Of(union.Read with { Uncached = true }) : union;
    }

    // A miss sets a hint only after one final master read.
    private async ValueTask<PathRead<T>> MissAsync(string key, string n, string identity, CancellationToken cancellationToken)
    {
        var again = Interpret(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), _p.Native);
        if (again.Kind == ReadOutcome.Absent)
        {
            _hints.Set(key, identity);
        }

        return again;
    }
}
