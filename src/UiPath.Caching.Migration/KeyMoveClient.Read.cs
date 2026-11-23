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
                KeyMoveMode.S1 => await ReadLegacyOnlyAsync(key, plan, cancellationToken).ConfigureAwait(false),
                KeyMoveMode.S3 => await PromoteAsync(key, plan, cancellationToken).ConfigureAwait(false),
                KeyMoveMode.S5 => await ReadUnionAsync(key, withSource: false, cancellationToken).ConfigureAwait(false),
                KeyMoveMode.S2 => await ReadLegacyOnlyAsync(key, plan, cancellationToken).ConfigureAwait(false),
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
        }
    }

    // S1 and S2 read only L before promotion starts. On a NoOrigin miss, check the identity before returning because a promotion may have moved the key.
    private async ValueTask<ReadStep<T>> ReadLegacyOnlyAsync(string key, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        var snap = await SnapAsync(InPlace(key), replica: false, SchedulePointStep.ReadSource, cancellationToken).ConfigureAwait(false);
        var read = Interpret(snap, _p.InPlace);
        if (read.Kind != ReadOutcome.Absent || !_p.NoOrigin || Data is not { } data)
        {
            return ReadStep<T>.Of(read);
        }

        string? id;
        try
        {
            await At(SchedulePointStep.ControlCheck).ConfigureAwait(false);
            id = await data.Db.HashGetAsync(ControlOf(data, InPlace(key)), "id", Master).ConfigureAwait(false);
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

    // N on a replica, N on the master, then L and N on the master again: a replica miss is never a miss before the primary agrees, and forward N is the destination.
    private async ValueTask<ReadStep<T>> ReadUnionAsync(string key, bool withSource, CancellationToken cancellationToken)
    {
        var n = Native(key);
        var first = Interpret(await SnapAsync(n, replica: true, SchedulePointStep.ReadDestination, cancellationToken).ConfigureAwait(false), _p.Native);
        if (first.Kind != ReadOutcome.Absent)
        {
            return ReadStep<T>.Of(first);
        }

        var master = Interpret(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), _p.Native);
        if (master.Kind != ReadOutcome.Absent || !withSource)
        {
            return ReadStep<T>.Of(master);
        }

        var source = Interpret(await SnapAsync(InPlace(key), replica: false, SchedulePointStep.ReadSource, cancellationToken).ConfigureAwait(false), _p.InPlace);
        if (source.Kind != ReadOutcome.Absent)
        {
            return ReadStep<T>.Of(source);
        }

        return ReadStep<T>.Of(Interpret(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), _p.Native));
    }
}
