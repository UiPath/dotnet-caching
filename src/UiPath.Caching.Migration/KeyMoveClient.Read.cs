using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveClient<T>
{
    public ValueTask<PathRead<T>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var plan = PlanFor(write: false);
        return plan.Mode == KeyMoveMode.Plain ? _p.Plain.ReadAsync(key, cancellationToken) : ReadManagedAsync(key, plan, cancellationToken);
    }

    private async ValueTask<PathRead<T>> ReadManagedAsync(string key, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        var run = 0;
        while (true)
        {
            var step = plan.Mode switch
            {
                KeyMoveMode.Preparing => await ReadLegacyOnlyAsync(key, plan, cancellationToken).ConfigureAwait(false),
                KeyMoveMode.Copying => await PromoteAsync(key, plan, cancellationToken).ConfigureAwait(false),
                KeyMoveMode.Done => await ReadUnionAsync(key, withSource: false, cancellationToken).ConfigureAwait(false),
                KeyMoveMode.Switching => await ReadLegacyOnlyAsync(key, plan, cancellationToken).ConfigureAwait(false),
                _ when KeyMovePlanning.PromotesReverse(plan) => await ReadReverseAsync(key, plan, cancellationToken).ConfigureAwait(false),
                _ => await ReadUnionAsync(key, withSource: true, cancellationToken).ConfigureAwait(false),
            };
            if (!step.Rerun)
            {
                return step.Read;
            }

            Count(IdentityRerun);
            if (run++ >= _options.MaxReruns)
            {
                return PathRead<T>.Of(ReadOutcome.Unavailable);
            }

            plan = PlanFor(write: false);
            if (plan.Mode == KeyMoveMode.Plain)
            {
                return await _p.Plain.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            }

            run++;
        }
    }

    // preparing and switching read only L before promotion starts. On a NoOrigin miss, check the identity before returning because promotion may have moved the key.
    private async ValueTask<ReadStep<T>> ReadLegacyOnlyAsync(string key, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        var snap = await SourceSnapAsync(key, SchedulePointStep.ReadSource, cancellationToken).ConfigureAwait(false);
        var read = Interpret(snap, Source.Codec);
        if (read.Kind != ReadOutcome.Absent || !_p.NoOrigin || Data is null)
        {
            return ReadStep<T>.Of(read);
        }

        string? id;
        try
        {
            await At(SchedulePointStep.ControlCheck).ConfigureAwait(false);
            id = await Source.ControlIdAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The state of the key cannot be read: the blind rule, union N-first without promotion.
            return await ReadUnionAsync(key, withSource: true, cancellationToken).ConfigureAwait(false);
        }

        // A record that is absent or lost is not "unchanged": it follows the unreadable branch, the blind union rule.
        if (id is null)
        {
            return await ReadUnionAsync(key, withSource: true, cancellationToken).ConfigureAwait(false);
        }

        return id != plan.Act?.Id ? ReadStep<T>.Again : ReadStep<T>.Of(read);
    }

    // In copying-back, read N on the master, serve a hit, then promote it to L in the same unit. A miss reads L without re-reading: reverse promoters publish L before deleting N.
    private async ValueTask<ReadStep<T>> ReadReverseAsync(string key, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        var native = Interpret(await SnapAsync(Native(key), replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), _p.Native, authoritativeNull: true);
        if (native.Kind == ReadOutcome.Found)
        {
            await PromoteReverseAsync(key, plan, cancellationToken).ConfigureAwait(false);
            return ReadStep<T>.Of(native);
        }

        if (!native.FallsThrough)
        {
            return ReadStep<T>.Of(native);
        }

        // No negative hint here: a miss does no promotion work for a hint to save, and a hint would skip the read of an L that a reverse writer has just set.
        return ReadStep<T>.Of(Interpret(await SourceSnapAsync(key, SchedulePointStep.ReadSource, cancellationToken).ConfigureAwait(false), Source.Codec));
    }

    // The value read is served whatever the promotion ends with; a promotion that does not finish is left to a later read or the mover.
    private async ValueTask PromoteReverseAsync(string key, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        if (_p.NoOrigin)
        {
            return;
        }

        try
        {
            var (admitted, _) = await AdmitAsync(promotion: true, plan.Act?.Id, cancellationToken).ConfigureAwait(false);
            using var unit = admitted;
            if (unit is null || Data is not { } data)
            {
                return;
            }

            for (var attempt = 0; attempt <= _options.MaxFenceRetries; attempt++)
            {
                var result = await _reversePromotion.RunAsync(key, null, unit, data, attempt, AlwaysOwned.Instance, cancellationToken).ConfigureAwait(false);
                if (result.Kind != PromotionKind.Restart)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            _ = ex;
        }
    }

    // N on a replica, N on the master, then L and N on the master again: a replica miss is never a miss before the primary agrees, and forward N is the destination.
    private async ValueTask<ReadStep<T>> ReadUnionAsync(string key, bool withSource, CancellationToken cancellationToken)
    {
        var n = Native(key);
        var first = Interpret(await SnapAsync(n, replica: true, SchedulePointStep.ReadDestination, cancellationToken).ConfigureAwait(false), _p.Native, authoritativeNull: true);
        if (!first.FallsThrough)
        {
            return ReadStep<T>.Of(first);
        }

        var master = Interpret(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), _p.Native, authoritativeNull: true);
        if (!master.FallsThrough || !withSource)
        {
            return ReadStep<T>.Of(master);
        }

        var source = Interpret(await SourceSnapAsync(key, SchedulePointStep.ReadSource, cancellationToken).ConfigureAwait(false), Source.Codec);
        if (source.Kind != ReadOutcome.Absent)
        {
            return ReadStep<T>.Of(source);
        }

        return ReadStep<T>.Of(Interpret(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), _p.Native, authoritativeNull: true));
    }
}
