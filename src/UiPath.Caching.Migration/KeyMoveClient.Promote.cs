using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveClient<T>
{
    private static int FirstDestinationProbe(bool probed) => probed ? 2 : 0;

    private static (bool Replica, SchedulePointStep Point) DestinationProbe(int read) =>
        read == 0 ? (true, SchedulePointStep.ReadDestination) : (false, SchedulePointStep.ReadDestinationOnMaster);

    private static ReadStep<T> PreferCurrent(PathRead<T> current, PathRead<T> uncached) =>
        ReadStep<T>.Of(current.Kind == ReadOutcome.Found ? current : uncached);

    // Forward read-through promotion in S3 starts after the router checks its local tier.
    private async ValueTask<ReadStep<T>> PromoteAsync(string key, KeyMovePlan plan, CancellationToken cancellationToken, bool probed = false)
    {
        try
        {
            var attempt = 0;
            ReadStep<T> step;
            do
            {
                var n = Native(key);
                var identity = plan.Identity;
                var alreadyProbed = probed && attempt == 0;
                // Batch reads arrive with the destination and source existence checked.
                for (var read = FirstDestinationProbe(alreadyProbed); read < 2; read++)
                {
                    var (replica, point) = DestinationProbe(read);
                    var found = Interpret(await SnapAsync(n, replica, point, cancellationToken).ConfigureAwait(false), _p.Native);
                    if (found.Kind != ReadOutcome.Absent)
                    {
                        return ReadStep<T>.Of(found);
                    }
                }

                if (CanUseMissHint(key, identity, alreadyProbed))
                {
                    return ReadStep<T>.Of(ReadOutcome.Absent);
                }

                // An absent source creates no fence. Recheck N in case another caller promoted.
                switch (await ProbeSource(key, alreadyProbed, cancellationToken).ConfigureAwait(false))
                {
                    case null:
                        return ReadStep<T>.Of(ReadOutcome.Unavailable);
                    case false:
                        return ReadStep<T>.Of(await MissAsync(key, n, identity, cancellationToken).ConfigureAwait(false));
                }

                step = await PromoteOnceAsync(key, n, identity, plan.Act?.Id, attempt++, cancellationToken).ConfigureAwait(false);
            }
            while (step.Restart);

            return step;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            return ReadStep<T>.Of(ReadOutcome.Unavailable);
        }
    }

    private ValueTask<bool?> ProbeSource(string key, bool probed, CancellationToken cancellationToken) =>
        probed ? ValueTask.FromResult<bool?>(true) : SourceExistsAsync(key, SchedulePointStep.ProbeSource, cancellationToken);

    private bool CanUseMissHint(string key, string identity, bool probed) => !probed && _hints.Has(key, identity);

    private async ValueTask<ReadStep<T>> PromoteOnceAsync(string key, string n, string identity, string? expectedId, int attempt, CancellationToken cancellationToken)
    {
        var (admitted, _) = await AdmitAsync(promotion: true, expectedId, cancellationToken).ConfigureAwait(false);
        using var unit = admitted;
        if (unit is null || Data is not { } data)
        {
            // When no unit is admitted under the rules' identity or the gate holds a different identity, skip publishing and read the union with a final check on N.
            return await UnpromotedAsync(key, cancellationToken).ConfigureAwait(false);
        }

        var keys = new FencedKeys(data.Names.ForwardFence(n), Key(n), ControlOf(data, n));

        // Capture the fence before taking the snapshot of L. The elapsed-time bound includes the CAP round trip.
        var events = data.ConnectionEvents();
        var started = _p.Clock.GetTimestamp();
        await At(SchedulePointStep.Capture).ConfigureAwait(false);
        var capture = await data.Scripts.CaptureAsync(keys, unit.Id, _p.CacheId, MoveScripts.NewNonce(), (long)_options.FenceTtl.TotalMilliseconds).ConfigureAwait(false);
        switch (capture)
        {
            case { Code: MoveScripts.IdentityMismatch } when unit.Readmit():
                return ReadStep<T>.Again;
            case { Code: MoveScripts.IdentityMismatch }:
                return await UnpromotedAsync(key, cancellationToken).ConfigureAwait(false);
            case { Captured: false }:
                // A clear may have closed the gate. Leave the miss hint alone.
                return ReadStep<T>.Of(ReadOutcome.Absent);
            case { DestinationExists: true }:
                return ReadStep<T>.Of(Interpret(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), _p.Native));
        }

        // Read L after the capture.
        var snap = await SourceSnapAsync(key, SchedulePointStep.ReadSource, cancellationToken).ConfigureAwait(false);
        var replied = _p.Clock.GetTimestamp();
        if (snap.Kind == ReadOutcome.Absent)
        {
            return ReadStep<T>.Of(await MissAsync(key, n, identity, cancellationToken).ConfigureAwait(false));
        }

        var (served, classification) = DecodeSource(snap);
        if (served is not { Kind: ReadOutcome.Found, Value: { } value })
        {
            return ReadStep<T>.Of(served);
        }

        if (ShouldServeWithoutPublishing(key, snap.TtlMs, classification, value, data, events, started, replied, out var encoded, out var cacheLocally))
        {
            return ReadStep<T>.Of(served with { Uncached = !cacheLocally });
        }

        var uncached = served with { Uncached = true };
        var publication = CapturedPublication(capture, snap.TtlMs);
        await At(SchedulePointStep.Publish).ConfigureAwait(false);
        var published = await data.Scripts.PublishAsync(keys, unit.Id, encoded, publication).ConfigureAwait(false);
        switch (published)
        {
            case MoveScripts.DestinationExists:
                var current = Interpret(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), _p.Native);
                return PreferCurrent(current, uncached);
            case MoveScripts.Published:
                break;
            default:
                return PublishRefusal(published, attempt, unit, uncached);
        }

        // Confirm N exists before deleting L, and delete L only if its bytes still match the source snapshot.
        var exists = await ExistsAsync(n, SchedulePointStep.ConfirmDestination, cancellationToken).ConfigureAwait(false);
        if (exists != true)
        {
            return ReadStep<T>.Of(served);
        }

        await At(SchedulePointStep.CompareDelete).ConfigureAwait(false);
        try
        {
            _ = await Source.CompareDeleteAsync(key, unit.Id, snap.Bytes ?? []).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            // The surviving source copy is lower priority than N.
        }

        // A successful promotion emits no event.
        return ReadStep<T>.Of(served);
    }

    private (PathRead<T> Read, DecodeClass Classification) DecodeSource(KeyMoveSnap snap)
    {
        if (snap.Kind != ReadOutcome.Found)
        {
            if (snap.Kind == ReadOutcome.Unreadable)
            {
                Count(UnreadableSource);
            }

            return (PathRead<T>.Of(snap.Kind), DecodeClass.Unreadable);
        }

        Decoded<T> decoded;
        try
        {
            decoded = Source.Codec.Decode(snap.Bytes ?? []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            decoded = new Decoded<T>(DecodeClass.Unreadable, null);
        }

        if (decoded.Class == DecodeClass.Expired)
        {
            return (PathRead<T>.Of(ReadOutcome.Absent), decoded.Class);
        }

        if (decoded.Class == DecodeClass.Unreadable || decoded.Value is null)
        {
            Count(UnreadableSource);
            return (PathRead<T>.Of(ReadOutcome.Unreadable), decoded.Class);
        }

        var expiration = snap.TtlMs < 0 ? (DateTimeOffset?)null : snap.At.AddMilliseconds(snap.TtlMs);
        return (new PathRead<T>(ReadOutcome.Found, decoded.Value, expiration), decoded.Class);
    }

    private bool ShouldServeWithoutPublishing(string key, long ttl, DecodeClass classification, T value, KeyMoveData data, long events, long started, long replied, out byte[] encoded, out bool cacheLocally)
    {
        encoded = [];
        cacheLocally = classification == DecodeClass.TooLarge || !Source.Maps(key);
        if (cacheLocally || PromotionTimedOut(data, events, started, replied) || ttl is >= 0 and < 1)
        {
            return true;
        }

        try
        {
            encoded = _p.Native.Encode(value);
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;
        }
    }

    private bool PromotionTimedOut(KeyMoveData data, long events, long started, long replied)
    {
        if (_p.Clock.GetElapsedTime(started, replied) > _options.CaptureToSnapshotMax)
        {
            Count("capture_slow");
            return true;
        }

        if (_p.Clock.GetElapsedTime(started) > _options.PromotionBudget || data.ConnectionEvents() != events)
        {
            Count("promotion_abandoned");
            return true;
        }

        return false;
    }

    private ReadStep<T> PublishRefusal(long code, int attempt, AdmittedUnit unit, PathRead<T> uncached)
    {
        switch (code)
        {
            case MoveScripts.FenceMoved when attempt < _options.MaxFenceRetries:
                return ReadStep<T>.Retry;
            case MoveScripts.FenceMoved:
                Count("fence_refused");
                break;
            case MoveScripts.IdentityMismatch when unit.Readmit():
                return ReadStep<T>.Again;
            case MoveScripts.PastDeadline:
                Count("promotion_expiring");
                break;
        }

        return ReadStep<T>.Of(uncached);
    }

    private Publication CapturedPublication(Capture capture, long ttl) =>
        ttl < 0 ? PublicationFor(capture, MoveScripts.NoExpiry, MoveScripts.NoExpiry) : PublicationFor(capture, ttl, capture.Tcap);

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

    // If the source probe or snapshot misses, read N on the master again and set a hint only if N is still absent.
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
