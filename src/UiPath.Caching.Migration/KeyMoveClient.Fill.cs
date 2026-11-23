using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveClient<T>
{
    public ValueTask<WriteResult> FillAsync(string key, T value, DateTimeOffset? deadline, FillBox<T>? stored, CancellationToken cancellationToken)
    {
        var plan = PlanFor(write: true);
        return plan.Mode == KeyMoveMode.Plain ? _p.Plain.FillAsync(key, value, deadline, stored, cancellationToken) : FillManagedAsync(key, value, deadline, stored, plan, cancellationToken);
    }

    private static long CreatedCode(bool created) => created ? MoveScripts.Published : MoveScripts.DestinationExists;

    private static FillBox<T>? BoxForWinner(long code, FillBox<T>? box) => code == MoveScripts.DestinationExists ? box : null;

    private static (WriteResult Result, bool Rerun) FillOutcome(long code, AdmittedUnit unit) => code switch
    {
        MoveScripts.IdentityMismatch when unit.Readmit() => (default, true),
        MoveScripts.IdentityMismatch or MoveScripts.DestinationExists => (WriteOutcome.NotSent, false),
        _ => (WriteOutcome.Committed, false),
    };

    private static void KeepFillWinner(FillBox<T> box, PathRead<T> held)
    {
        if (held.Kind == ReadOutcome.Found)
        {
            box.Stored = held.Value;
            box.StoredExpiration = held.Expiration;
        }
        else
        {
            box.Stored = null;
            box.StoredExpiration = null;
        }
    }

    // A fill uses NX to write a value just made by the origin. If the key exists, the fill writes nothing and returns the stored value in the box.
    private async ValueTask<WriteResult> FillManagedAsync(string key, T value, DateTimeOffset? deadline, FillBox<T>? box, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        var px = deadline is { } at ? MillisecondsLeft(at) : MoveScripts.NoExpiry;
        if (deadline is not null && px < 1)
        {
            return WriteOutcome.NotSent;
        }

        var run = 0;
        while (true)
        {
            plan = await SettleWaitAsync(plan, cancellationToken).ConfigureAwait(false);
            if (plan.Mode == KeyMoveMode.Plain)
            {
                return await _p.Plain.FillAsync(key, value, deadline, box, cancellationToken).ConfigureAwait(false);
            }

            if (CreateIsClosed(plan.Mode))
            {
                throw Unavailable();
            }

            var (admitted, replan) = await AdmitAsync(promotion: false, plan.Act?.Id, cancellationToken).ConfigureAwait(false);
            if (replan)
            {
                CheckAdmissionRerun(run++);
                await Task.Delay(_options.AdmissionPoll, _p.Clock, cancellationToken).ConfigureAwait(false);
                plan = PlanFor(write: true);
                continue;
            }

            using var unit = admitted ?? throw Unavailable();
            var (result, rerun) = await FillOnceAsync(key, value, px, plan.Mode, unit, box, cancellationToken).ConfigureAwait(false);
            if (!rerun)
            {
                return result;
            }

            Count(IdentityRerun);
            if (run++ >= _options.MaxReruns)
            {
                return WriteOutcome.NotSent;
            }

            plan = PlanFor(write: true);
        }
    }

    private async ValueTask<(WriteResult Result, bool Rerun)> FillOnceAsync(string key, T value, long px, KeyMoveMode mode, AdmittedUnit unit, FillBox<T>? box, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || Data is not { } data || !data.IsConnected())
        {
            return (WriteOutcome.NotSent, false);
        }

        var l = InPlace(key);
        var n = Native(key);
        if (EncodeFill(value, mode) is not { } bytes)
        {
            return (WriteOutcome.NotSent, false);
        }

        long code;
        try
        {
            switch (mode)
            {
                case KeyMoveMode.S1:
                case KeyMoveMode.S2:
                    await At(SchedulePointStep.SourceSet).ConfigureAwait(false);
                    code = await data.Scripts.SourceSetAsync(Key(l), ControlOf(data, l), unit.Id, bytes, px, true).ConfigureAwait(false);
                    break;
                case KeyMoveMode.S5:
                    await At(SchedulePointStep.NativeWrite).ConfigureAwait(false);
                    code = CreatedCode(await data.Db.StringSetAsync(Key(n), bytes, Expiry(px), When.NotExists, Master).ConfigureAwait(false));
                    break;
                default:
                    await At(SchedulePointStep.Fill).ConfigureAwait(false);
                    code = await data.Scripts.FillAsync(Fenced(data, n), unit.Id, bytes, px, MoveScripts.NewNonce(), FenceTtlMs).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (Typed(WriteOutcome.Unknown), false);
        }

        if (BoxForWinner(code, box) is { } winner)
        {
            // The losing fill returns the winner's value under the original fill mark.
            var source = mode is KeyMoveMode.S1 or KeyMoveMode.S2;
            var (keyToRead, point, codec) = source ? (l, SchedulePointStep.ReadSource, _p.InPlace) : (n, SchedulePointStep.ReadDestinationOnMaster, _p.Native);
            var held = Interpret(await SnapAsync(keyToRead, replica: false, point, cancellationToken).ConfigureAwait(false), codec);
            KeepFillWinner(winner, held);
        }

        return FillOutcome(code, unit);
    }

    private bool CreateIsClosed(KeyMoveMode mode) => mode is KeyMoveMode.Closed or KeyMoveMode.Union || (mode == KeyMoveMode.S2 && _p.NoOrigin);

    private byte[]? EncodeFill(T value, KeyMoveMode mode)
    {
        try
        {
            return mode is KeyMoveMode.S1 or KeyMoveMode.S2 ? _p.InPlace.Encode(value) : _p.Native.Encode(value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            return null;
        }
    }

}
