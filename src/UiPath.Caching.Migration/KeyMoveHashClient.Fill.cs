using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveHashClient<T>
{
    public ValueTask<WriteResult> FillAsync(string key, IDictionary<string, T?> fields, HashWriteOptions options, HashFillBox<T>? stored, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var plan = PlanFor(write: true);
        if (plan.Mode == KeyMoveMode.Plain)
        {
            return _p.Plain.FillAsync(key, fields, options, stored, cancellationToken);
        }

        ThrowIfMetadataRefused(options.Metadata, plan.Mode);

        return FillManagedAsync(key, fields, options, stored, plan, cancellationToken);
    }

    private static ValueTask DiscardStagingAsync(FillAttempt fill) => HashStaging.DeleteAsync(fill.Data, fill.Staging);

    // A fill is an NX write of a whole hash the origin just made: it never clobbers and never erases. A fill that loses returns the stored hash in the box and writes nothing.
    private async ValueTask<WriteResult> FillManagedAsync(string key, IDictionary<string, T?> fields, HashWriteOptions options, HashFillBox<T>? box, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        // The deadline stays absolute to the publication: staging, admission and reruns all take time, and the script refuses a fill whose deadline has passed.
        if (FillExpireAt(options) is not { } expireAt)
        {
            return WriteOutcome.NotSent;
        }

        var run = -1;
        while (true)
        {
            run++;
            plan = await SettleWaitAsync(plan, cancellationToken).ConfigureAwait(false);
            if (plan.Mode == KeyMoveMode.Plain)
            {
                return await _p.Plain.FillAsync(key, fields, options, box, cancellationToken).ConfigureAwait(false);
            }

            // The state can have changed under a wait or a new plan: the capability of L is judged again before anything is encoded or sent.
            ThrowIfMetadataRefused(options.Metadata, plan.Mode);

            ThrowIfNoManagedWrite(plan);
            var (admitted, replan) = Admit(promotion: false, plan.Act?.Id);
            if (replan)
            {
                plan = await ReplanAfterAdmissionAsync(run, cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var unit = admitted ?? throw Unavailable();
            var (result, rerun) = await FillOnceAsync(key, fields, options.Metadata, expireAt, plan.Mode, unit, box, cancellationToken).ConfigureAwait(false);
            if (!rerun)
            {
                return result;
            }

            Count("identity_rerun");
            if (run >= _options.MaxReruns)
            {
                return WriteOutcome.NotSent;
            }

            plan = PlanFor(write: true);
        }
    }

    // The absolute expiry of a fill, or none; null when the deadline has already passed.
    private long? FillExpireAt(HashWriteOptions options)
    {
        if (options.Deadline is not { } at)
        {
            return MoveScripts.NoExpiry;
        }

        return (long)Math.Ceiling((at - _p.Clock.GetUtcNow()).TotalMilliseconds) < 1 ? null : at.ToUnixTimeMilliseconds();
    }

    // No state in which a managed write can run: closed, a union read, a reverse state, or a cache with no origin before its keys move.
    private void ThrowIfNoManagedWrite(KeyMovePlan plan)
    {
        if (plan.Mode is KeyMoveMode.Closed or KeyMoveMode.Union || KeyMovePlanning.IsReverseWrite(plan.Mode) || (plan.Mode == KeyMoveMode.S2 && _p.NoOrigin))
        {
            throw Unavailable();
        }
    }

    // The steps were built under an older state: they are built again under the one in force, after a short wait.
    private async ValueTask<KeyMovePlan> ReplanAfterAdmissionAsync(int run, CancellationToken cancellationToken)
    {
        if (run >= _options.MaxReruns)
        {
            throw Unavailable();
        }

        await Task.Delay(_options.AdmissionPoll, _p.Clock, cancellationToken).ConfigureAwait(false);
        return PlanFor(write: true);
    }

    private async ValueTask<(WriteResult Result, bool Rerun)> FillOnceAsync(string key, IDictionary<string, T?> fields, IDictionary<string, string?>? metadata, long expireAt, KeyMoveMode mode, AdmittedUnit unit, HashFillBox<T>? box, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || Data is not { } data || !data.IsConnected())
        {
            return (WriteOutcome.NotSent, false);
        }

        var l = InPlace(key);
        var n = Native(key);
        var inPlace = mode is KeyMoveMode.S1 or KeyMoveMode.S2;
        if (!TryEncodeFill(fields, metadata, inPlace, out var stored) || stored.Count == 0)
        {
            return (WriteOutcome.NotSent, false);
        }

        // The staging key sits in the destination's slot: L's for S1 and S2, N's otherwise.
        var staging = inPlace ? data.Names.ReverseStaging(l, MoveScripts.NewNonce()) : data.Names.ForwardStaging(n, MoveScripts.NewNonce());
        var fill = new FillAttempt(data, l, n, staging, expireAt, mode, unit);
        var (code, early) = await SendFillAsync(fill, stored, cancellationToken).ConfigureAwait(false);
        if (early is { } settled)
        {
            return (settled, false);
        }

        return await JudgeFillAsync(fill, code, box, cancellationToken).ConfigureAwait(false);
    }

    // False when the fields cannot be encoded; a hash with nothing to store comes back empty.
    private bool TryEncodeFill(IDictionary<string, T?> fields, IDictionary<string, string?>? metadata, bool inPlace, out IReadOnlyList<KeyValuePair<string, byte[]>> stored)
    {
        try
        {
            var inPlaceMetadata = _p.InPlace.CarriesMetadata ? metadata : null;
            stored = inPlace ? _p.InPlace.EncodeFields(fields, inPlaceMetadata) : _p.Native.EncodeFields(fields, metadata);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            stored = [];
            return false;
        }
    }

    // Stages the hash and runs the fill script; the early result is set where the fill settles before or without a script reply.
    private async ValueTask<(long Code, WriteResult? Early)> SendFillAsync(FillAttempt fill, IReadOnlyList<KeyValuePair<string, byte[]>> stored, CancellationToken cancellationToken)
    {
        try
        {
            await At(SchedulePointStep.StageHash).ConfigureAwait(false);
            if (!await HashStaging.WriteAsync(fill.Data, fill.Staging, stored, _options.FenceTtl).ConfigureAwait(false))
            {
                await DiscardStagingAsync(fill).ConfigureAwait(false);
                return (0, WriteOutcome.NotSent);
            }

            await At(SchedulePointStep.Fill).ConfigureAwait(false);

            // Staging and admission took time; a server without scripts cannot compare the deadline itself, so it is judged here too.
            if (fill.ExpireAt != MoveScripts.NoExpiry && _p.Clock.GetUtcNow().ToUnixTimeMilliseconds() >= fill.ExpireAt)
            {
                await DiscardStagingAsync(fill).ConfigureAwait(false);
                return (0, WriteOutcome.NotSent);
            }

            return (await FillScriptAsync(fill).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await DiscardStagingAsync(fill).ConfigureAwait(false);
            return (0, Typed(WriteOutcome.Unknown));
        }
    }

    private async ValueTask<long> FillScriptAsync(FillAttempt fill)
    {
        var (data, l, n, staging, expireAt, unit) = (fill.Data, fill.L, fill.N, fill.Staging, fill.ExpireAt, fill.Unit);
        return fill.Mode switch
        {
            KeyMoveMode.S1 or KeyMoveMode.S2 => await data.Scripts.FillHashUntilAsync(Key(l), ControlOf(data, l), staging, unit.Id, expireAt, null).ConfigureAwait(false),
            KeyMoveMode.S5 => await data.Scripts.FillHashUntilAsync(Key(n), ControlOf(data, n), staging, unit.Id, expireAt, null).ConfigureAwait(false),
            _ => await data.Scripts.FillHashUntilAsync(Key(n), ControlOf(data, n), staging, unit.Id, expireAt, new FenceBump(data.Names.ForwardFence(n), MoveScripts.NewNonce(), FenceTtlMs)).ConfigureAwait(false),
        };
    }

    // The script's answer: only a published fill commits; a lost fill hands back the stored hash.
    private async ValueTask<(WriteResult Result, bool Rerun)> JudgeFillAsync(FillAttempt fill, long code, HashFillBox<T>? box, CancellationToken cancellationToken)
    {
        if (code == MoveScripts.IdentityMismatch)
        {
            await DiscardStagingAsync(fill).ConfigureAwait(false);
            return fill.Unit.Readmit() ? (default, true) : (WriteOutcome.NotSent, false);
        }

        if (code == MoveScripts.DestinationExists)
        {
            // The fill lost: the hash that is stored is returned and kept under the fill mark.
            if (box is not null)
            {
                await RecordLostFillAsync(fill, box, cancellationToken).ConfigureAwait(false);
            }

            return (WriteOutcome.NotSent, false);
        }

        if (code != MoveScripts.Published)
        {
            // The deadline had passed, or the staging key was gone, which only an expiry does: nothing was written.
            await DiscardStagingAsync(fill).ConfigureAwait(false);
            return (WriteOutcome.NotSent, false);
        }

        return (WriteOutcome.Committed, false);
    }

    private async ValueTask RecordLostFillAsync(FillAttempt fill, HashFillBox<T> box, CancellationToken cancellationToken)
    {
        var held = fill.InPlace
            ? HashReads<T>.Interpret(await _reads.SnapAsync(fill.L, replica: false, SchedulePointStep.ReadSource, cancellationToken).ConfigureAwait(false), _p.InPlace)
            : HashReads<T>.Interpret(await _reads.SnapAsync(fill.N, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), _p.Native, authoritativeNull: true);
        box.Lost = true;
        box.CachedEmpty = held.Stops;
        box.Stored = held.Kind == ReadOutcome.Found ? held.Fields : null;
        box.StoredExpiration = held.Kind == ReadOutcome.Found ? held.Expiration : null;
        box.Unreadable = held.Kind == ReadOutcome.Unreadable;
    }

    private readonly record struct FillAttempt(KeyMoveData Data, string L, string N, RedisKey Staging, long ExpireAt, KeyMoveMode Mode, AdmittedUnit Unit)
    {
        public bool InPlace => Mode is KeyMoveMode.S1 or KeyMoveMode.S2;
    }
}
