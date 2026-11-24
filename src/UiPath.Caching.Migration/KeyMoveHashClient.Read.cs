using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveHashClient<T>
{
    public ValueTask<HashRead<T>> ReadAsync(string key, string[]? fields, CancellationToken cancellationToken)
    {
        var plan = PlanFor(write: false);
        return plan.Mode == KeyMoveMode.Plain ? _p.Plain.ReadAsync(key, fields, cancellationToken) : ReadManagedAsync(key, fields, plan, cancellationToken);
    }

    public async ValueTask<bool?> ContainsAsync(string key, CancellationToken cancellationToken)
    {
        if (!Moving)
        {
            return await _p.Plain.ContainsAsync(key, cancellationToken).ConfigureAwait(false);
        }

        return await HolderAsync(key).ConfigureAwait(false) is not null;
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken cancellationToken)
    {
        if (!Moving)
        {
            return await _p.Plain.ExpireTimeAsync(key, cancellationToken).ConfigureAwait(false);
        }

        return await HolderAsync(key).ConfigureAwait(false) is { } life && life.Left >= 0 ? _p.Clock.GetUtcNow().AddMilliseconds(life.Left) : null;
    }

    // The sides the read plan reads, in its order: S5 only N, S1 and S2 only L, otherwise N, L, then N again. Only the life and the existence are asked, so nothing is decoded or promoted.
    private async ValueTask<(string Side, long Left)?> HolderAsync(string key)
    {
        var data = Data;
        if (data is null || !data.IsConnected())
        {
            throw new CacheUnavailableException($"Cache '{_p.CacheId}' could not answer a read of the hash.");
        }

        try
        {
            string[] sides = PlanFor(write: false).Mode switch
            {
                KeyMoveMode.S5 => [Native(key)],
                KeyMoveMode.S1 or KeyMoveMode.S2 => [InPlace(key)],
                _ => [Native(key), InPlace(key), Native(key)],
            };
            foreach (var side in sides)
            {
                // A life is asked for first, and only a key with none is asked whether it exists.
                var life = await data.Db.KeyTimeToLiveAsync(Key(side), Master).ConfigureAwait(false);
                long left;
                if (life is { } span)
                {
                    left = (long)Math.Ceiling(span.TotalMilliseconds);
                }
                else
                {
                    left = await data.Db.KeyExistsAsync(Key(side), Master).ConfigureAwait(false) ? -1 : -2;
                }

                if (left != -2)
                {
                    return (side, left);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new CacheUnavailableException($"Cache '{_p.CacheId}' could not answer a read of the hash.", ex);
        }
    }

    private async ValueTask<HashRead<T>> ReadManagedAsync(string key, string[]? fields, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        var run = -1;
        while (true)
        {
            run++;
            var step = plan.Mode switch
            {
                KeyMoveMode.S1 or KeyMoveMode.S2 => await ReadLegacyOnlyAsync(key, plan, cancellationToken).ConfigureAwait(false),
                KeyMoveMode.S3 => await PromoteReadAsync(key, cancellationToken).ConfigureAwait(false),
                KeyMoveMode.S5 => await _reads.ReadUnionAsync(key, withSource: false, cancellationToken).ConfigureAwait(false),
                _ => await _reads.ReadUnionAsync(key, withSource: true, cancellationToken).ConfigureAwait(false),
            };
            if (!step.Rerun)
            {
                return fields is null ? step.Read : step.Read.Only(fields);
            }

            Count("identity_rerun");
            if (run >= _options.MaxReruns)
            {
                return HashRead<T>.Of(ReadOutcome.Unavailable);
            }

            plan = PlanFor(write: false);
            if (plan.Mode == KeyMoveMode.Plain)
            {
                return await _p.Plain.ReadAsync(key, fields, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<HashReadStep<T>> PromoteReadAsync(string key, CancellationToken cancellationToken)
    {
        var promoted = await _promotion.PromoteAsync(key, HashPromotionFlavour.Read, cancellationToken).ConfigureAwait(false);
        return promoted.Status == HashPromotionStatus.Replan ? HashReadStep<T>.Again : HashReadStep<T>.Of(promoted.Read);
    }

    // S1 and S2 while nothing is promoted: L only; a NoOrigin miss reads the identity first, because a promotion may have moved the key.
    private async ValueTask<HashReadStep<T>> ReadLegacyOnlyAsync(string key, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        var snap = await _reads.SnapAsync(InPlace(key), replica: false, SchedulePointStep.ReadSource, cancellationToken).ConfigureAwait(false);
        var read = HashReads<T>.Interpret(snap, _p.InPlace);
        if (read.Kind != ReadOutcome.Absent || !_p.NoOrigin || Data is not { } data)
        {
            return HashReadStep<T>.Of(read);
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
            return await _reads.ReadUnionAsync(key, withSource: true, cancellationToken).ConfigureAwait(false);
        }

        // A record that is absent or lost is not "unchanged": it follows the unreadable branch, the blind union rule.
        if (id is null)
        {
            return await _reads.ReadUnionAsync(key, withSource: true, cancellationToken).ConfigureAwait(false);
        }

        return id != plan.Act?.Id ? HashReadStep<T>.Again : HashReadStep<T>.Of(read);
    }
}
