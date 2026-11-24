using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The per-key promotion (CAP, the snapshot of L through the source side, decode, encode, PUB, EXISTS, CMPDEL, and the capture-to-snapshot bound), shared by the read-through and the mover.</summary>
/// <remarks>A run with no <see cref="MoverFlavor"/> is the read-through and behaves exactly as the client always did. L is reached only through <see cref="IMoveSide{T}"/>.</remarks>
internal sealed class KeyPromotion<T>
    where T : class
{
    private const CommandFlags Master = CommandFlags.DemandMaster;

    private readonly KeyMoveClientParts<T> _p;
    private readonly IMoveSide<T> _source;
    private readonly KeyMoveOptions _options;
    private readonly ISupersessionHandler? _readThrough;

    /// <param name="readThrough">Told when a read-through's capture meets a live supersession marker; the mover's handler comes with its <see cref="MoverFlavor"/>.</param>
    public KeyPromotion(KeyMoveClientParts<T> parts, IMoveSide<T> source, ISupersessionHandler? readThrough = null)
    {
        _p = parts ?? throw new ArgumentNullException(nameof(parts));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = parts.Options;
        _readThrough = readThrough;
    }

    private long FenceTtlMs => (long)_options.FenceTtl.TotalMilliseconds;

    public static PathRead<T> Interpret(KeyMoveSnap snap, IKeyMoveCodec<T> codec)
    {
        if (snap.Kind != ReadOutcome.Found)
        {
            return PathRead<T>.Of(snap.Kind);
        }

        Decoded<T> decoded;
        try
        {
            decoded = codec.Decode(snap.Bytes ?? []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PathRead<T>.Of(ReadOutcome.Unreadable);
        }

        return decoded.Class switch
        {
            DecodeClass.Expired => PathRead<T>.Of(ReadOutcome.Absent),
            DecodeClass.Unreadable => PathRead<T>.Of(ReadOutcome.Unreadable),
            _ when decoded.Value is null => PathRead<T>.Of(ReadOutcome.Unreadable),
            _ => new PathRead<T>(ReadOutcome.Found, decoded.Value, snap.TtlMs < 0 ? null : snap.At.AddMilliseconds(snap.TtlMs)),
        };
    }

    public async ValueTask<KeyMoveSnap> SnapAsync(string key, bool replica, SchedulePointStep step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_p.Plane.Data is not { } data || !data.IsConnected())
        {
            return KeyMoveSnap.Of(ReadOutcome.Unavailable);
        }

        await At(step).ConfigureAwait(false);
        try
        {
            var read = await data.Db.StringGetWithExpiryAsync(key, replica ? CommandFlags.PreferReplica : Master).ConfigureAwait(false);
            if (read.Value.IsNull)
            {
                return KeyMoveSnap.Of(ReadOutcome.Absent);
            }

            var ttl = read.Expiry is { } left ? (long)Math.Ceiling(left.TotalMilliseconds) : -1;
            return new KeyMoveSnap(ReadOutcome.Found, (byte[]?)read.Value ?? [], ttl, _p.Clock.GetUtcNow());
        }
        catch (RedisServerException ex) when (ex.Message.Contains("WRONGTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return KeyMoveSnap.Of(ReadOutcome.Unreadable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return KeyMoveSnap.Of(ReadOutcome.Unavailable);
        }
    }

    public async ValueTask<bool?> ExistsAsync(string key, SchedulePointStep step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_p.Plane.Data is not { } data || !data.IsConnected())
        {
            return null;
        }

        await At(step).ConfigureAwait(false);
        try
        {
            return await data.Db.KeyExistsAsync(key, Master).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Promotes <paramref name="key"/> under <paramref name="unit"/>, which the caller admitted and disposes.</summary>
    /// <param name="mover">Null for a read-through.</param>
    /// <param name="attempt">How many times the fence has moved already for this key.</param>
    /// <param name="ownership">Asked before CAP, PUB and the compare-delete, the steps that write; the read-through passes <see cref="AlwaysOwned.Instance"/>.</param>
    public async ValueTask<PromotionResult<T>> RunAsync(string key, MoverFlavor? mover, AdmittedUnit unit, KeyMoveData data, int attempt, IMoveOwnership ownership, CancellationToken cancellationToken)
    {
        var n = _p.NativeKey(key);
        var run = new PromotionRun(key, n, new FencedKeys(data.Names.ForwardFence(n), n, data.Names.ControlFor(_p.Group, n)), mover, unit, data, attempt, ownership);

        // The capture comes before the snapshot of L, never in parallel. The bound counts from before CAP is sent, so a delayed CAP reply counts.
        var events = data.ConnectionEvents();
        var started = _p.Clock.GetTimestamp();
        var (capture, refused) = await CaptureAsync(run, cancellationToken).ConfigureAwait(false);
        if (refused is { } notCaptured)
        {
            return notCaptured;
        }

        var snap = await SourceSnapAsync(key, cancellationToken).ConfigureAwait(false);
        var replied = _p.Clock.GetTimestamp();
        var (served, skipped) = ReadSource(run, snap);
        if (skipped is { } skip)
        {
            return skip;
        }

        var uncached = served with { Uncached = true };
        if (AbandonReason(data, events, started, replied, snap.TtlMs) is { } reason)
        {
            return new PromotionResult<T>(PromotionKind.Abandoned, uncached, Reason: reason);
        }

        byte[] encoded;
        try
        {
            encoded = _p.Native.Encode(served.Value!);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new PromotionResult<T>(PromotionKind.Abandoned, uncached, Reason: "encode_failed");
        }

        var (drained, unpublished) = await PublishAsync(run, capture, encoded, snap.TtlMs, uncached, cancellationToken).ConfigureAwait(false);
        if (unpublished is { } stop)
        {
            return stop;
        }

        var lost = await DeleteSourceAsync(run, snap, cancellationToken).ConfigureAwait(false);
        return lost ?? PromotionResult<T>.Of(drained ? PromotionKind.Drained : PromotionKind.Promoted, served);
    }

    // Null for an always-owned caller, so the read-through's operations run exactly as they did.
    internal static Func<bool>? StillHeld(IMoveOwnership ownership) => ReferenceEquals(ownership, AlwaysOwned.Instance) ? null : () => ownership.StillHeld;

    private static Decoded<T> Decode(IKeyMoveCodec<T> codec, KeyMoveSnap snap)
    {
        try
        {
            return codec.Decode(snap.Bytes ?? []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Decoded<T>(DecodeClass.Unreadable, null);
        }
    }

    private async ValueTask<(Capture Capture, PromotionResult<T>? Refused)> CaptureAsync(PromotionRun run, CancellationToken cancellationToken)
    {
        await At(SchedulePointStep.Capture).ConfigureAwait(false);
        if (!await run.Ownership.HoldsAsync(cancellationToken).ConfigureAwait(false))
        {
            return (default, PromotionResult<T>.Of(PromotionKind.OwnershipLost));
        }

        Capture capture;
        try
        {
            capture = await run.Data.Scripts.CaptureAsync(run.Keys, run.Unit.Id, _p.CacheId, MoveScripts.NewNonce(), FenceTtlMs, MarkerField(run.Data, run.Key), StillHeld(run.Ownership)).ConfigureAwait(false);
        }
        catch (OwnershipLostException)
        {
            return (default, PromotionResult<T>.Of(PromotionKind.OwnershipLost));
        }

        return (capture, await RefusedCaptureAsync(run, capture, cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<PromotionResult<T>?> RefusedCaptureAsync(PromotionRun run, Capture capture, CancellationToken cancellationToken)
    {
        if (capture.Code == MoveScripts.IdentityMismatch)
        {
            return new PromotionResult<T>(PromotionKind.IdentityAtCapture, Readmitted: run.Unit.Readmit());
        }

        if (capture.Code == MoveScripts.LiveSupersession && (run.Mover?.Supersession ?? _readThrough) is { } handler)
        {
            return await SupersededAsync(run, handler, capture.Marker, cancellationToken).ConfigureAwait(false);
        }

        if (!capture.Captured)
        {
            // The cache gate is closed (a Clear is running) or absent: a miss, with no hint.
            Count("gate_closed");
            return PromotionResult<T>.Of(PromotionKind.GateClosed, ReadOutcome.Absent);
        }

        // A read-through serves the N that is there. The mover goes on, so that the L copy drains: its publish answers 2 and its compare-delete runs.
        if (capture.DestinationExists && run.Mover is null)
        {
            return PromotionResult<T>.Of(PromotionKind.NativeExists, await ReadNativeAsync(run.N, cancellationToken).ConfigureAwait(false));
        }

        return null;
    }

    private async ValueTask<PromotionResult<T>> SupersededAsync(PromotionRun run, ISupersessionHandler handler, RedisValue marker, CancellationToken cancellationToken)
    {
        var action = await handler.OnSupersededAsync(new SupersededKey(_p.Group, _p.CacheId, run.Key, marker, run.Unit.Id), cancellationToken).ConfigureAwait(false);
        return action switch
        {
            SupersessionAction.Retry => new PromotionResult<T>(PromotionKind.SupersededRetry),
            SupersessionAction.Rerun => new PromotionResult<T>(PromotionKind.SupersededRerun, Readmitted: run.Unit.Readmit()),
            _ => new PromotionResult<T>(PromotionKind.Superseded),
        };
    }

    // Reads the snapshot of L into the value to serve, or the result that ends the run before publishing.
    private (PathRead<T> Served, PromotionResult<T>? Skipped) ReadSource(PromotionRun run, KeyMoveSnap snap)
    {
        if (snap.Kind == ReadOutcome.Absent)
        {
            return (default, PromotionResult<T>.Of(PromotionKind.SourceAbsent));
        }

        if (snap.Kind != ReadOutcome.Found)
        {
            if (snap.Kind == ReadOutcome.Unreadable)
            {
                Count("unreadable_source");
            }

            return (default, PromotionResult<T>.Of(snap.Kind == ReadOutcome.Unreadable ? PromotionKind.Unreadable : PromotionKind.Unavailable, snap.Kind));
        }

        var decoded = Decode(_source.Codec, snap);
        if (decoded.Class == DecodeClass.Unreadable || (decoded.Class != DecodeClass.Expired && decoded.Value is null))
        {
            Count("unreadable_source");
            return (default, PromotionResult<T>.Of(PromotionKind.Unreadable, ReadOutcome.Unreadable));
        }

        if (decoded.Class == DecodeClass.Expired)
        {
            return (default, PromotionResult<T>.Of(PromotionKind.Expired, ReadOutcome.Absent));
        }

        var expiration = snap.TtlMs < 0 ? (DateTimeOffset?)null : snap.At.AddMilliseconds(snap.TtlMs);
        var served = new PathRead<T>(ReadOutcome.Found, decoded.Value, expiration);
        return (served, Unmoved(run, snap, decoded.Class, served));
    }

    private PromotionResult<T>? Unmoved(PromotionRun run, KeyMoveSnap snap, DecodeClass decoded, PathRead<T> served)
    {
        if (decoded == DecodeClass.TooLarge)
        {
            return PromotionResult<T>.Of(PromotionKind.TooLarge, served);
        }

        if (!_source.Maps(run.Key))
        {
            return PromotionResult<T>.Of(PromotionKind.Unmappable, served);
        }

        // A key with a count of life below the minimum is left to its next read; none is never short.
        if (run.Mover is { } mover && snap.TtlMs >= 0 && snap.TtlMs < mover.MinTtl.TotalMilliseconds)
        {
            return PromotionResult<T>.Of(PromotionKind.ShortTtl, served);
        }

        return null;
    }

    // Why the run abandons before publishing, serving the decoded value uncached; null to publish.
    private string? AbandonReason(KeyMoveData data, long events, long started, long replied, long ttlMs)
    {
        if (_p.Clock.GetElapsedTime(started, replied) > _options.CaptureToSnapshotMax)
        {
            Count("capture_slow");
            return "capture_slow";
        }

        if (_p.Clock.GetElapsedTime(started) > _options.PromotionBudget || data.ConnectionEvents() != events)
        {
            Count("promotion_abandoned");
            return "abandoned";
        }

        // A life that is a count below 1 ms is not published; none is published as none.
        return ttlMs is >= 0 and < 1 ? "abandoned" : null;
    }

    private async ValueTask<(bool Drained, PromotionResult<T>? Stopped)> PublishAsync(PromotionRun run, Capture capture, byte[] encoded, long ttlMs, PathRead<T> uncached, CancellationToken cancellationToken)
    {
        var px = ttlMs < 0 ? MoveScripts.NoExpiry : ttlMs;
        var publication = new Publication(capture.Token ?? throw new InvalidOperationException("A captured fence requires a token."), capture.Gate ?? throw new InvalidOperationException("A captured fence requires a gate."), _p.CacheId, MoveScripts.NewNonce(), FenceTtlMs, px, px == MoveScripts.NoExpiry ? MoveScripts.NoExpiry : capture.Tcap, true);
        await At(SchedulePointStep.Publish).ConfigureAwait(false);
        if (!await run.Ownership.HoldsAsync(cancellationToken).ConfigureAwait(false))
        {
            return (false, PromotionResult<T>.Of(PromotionKind.OwnershipLost));
        }

        long published;
        try
        {
            published = await run.Data.Scripts.PublishAsync(run.Keys, run.Unit.Id, encoded, publication, StillHeld(run.Ownership)).ConfigureAwait(false);
        }
        catch (OwnershipLostException)
        {
            return (false, PromotionResult<T>.Of(PromotionKind.OwnershipLost));
        }

        return await PublishOutcomeAsync(run, published, uncached, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<(bool Drained, PromotionResult<T>? Stopped)> PublishOutcomeAsync(PromotionRun run, long published, PathRead<T> uncached, CancellationToken cancellationToken)
    {
        switch (published)
        {
            case MoveScripts.Published:
                return (false, null);
            case MoveScripts.DestinationExists when run.Mover is null:
                var found = await ReadNativeAsync(run.N, cancellationToken).ConfigureAwait(false);
                return (false, PromotionResult<T>.Of(PromotionKind.PublishFoundNative, found is { Kind: ReadOutcome.Found } ? found : uncached));
            case MoveScripts.DestinationExists:
                // The duplicate runs its compare-delete anyway, so L drains.
                return (true, null);
            case MoveScripts.FenceMoved when run.Attempt < _options.MaxFenceRetries:
                // A mutation happened: start again.
                return (false, PromotionResult<T>.Of(PromotionKind.Restart));
            case MoveScripts.FenceMoved:
                Count("fence_refused");
                return (false, PromotionResult<T>.Of(PromotionKind.FenceRefused, uncached));
            case MoveScripts.IdentityMismatch:
                return (false, new PromotionResult<T>(PromotionKind.IdentityAtPublish, uncached, run.Unit.Readmit()));
            case MoveScripts.PastDeadline:
                Count("promotion_expiring");
                return (false, PromotionResult<T>.Of(PromotionKind.Expiring, uncached));
            default:
                return (false, PromotionResult<T>.Of(PromotionKind.Refused, uncached));
        }
    }

    // L stays as the copy unless N is there; the delete is a compare against the exact bytes read. Null unless ownership was lost.
    private async ValueTask<PromotionResult<T>?> DeleteSourceAsync(PromotionRun run, KeyMoveSnap snap, CancellationToken cancellationToken)
    {
        if (await ExistsAsync(run.N, SchedulePointStep.ConfirmDestination, cancellationToken).ConfigureAwait(false) != true)
        {
            return null;
        }

        await At(SchedulePointStep.CompareDelete).ConfigureAwait(false);
        if (!await run.Ownership.HoldsAsync(cancellationToken).ConfigureAwait(false))
        {
            return PromotionResult<T>.Of(PromotionKind.OwnershipLost);
        }

        try
        {
            _ = await _source.CompareDeleteAsync(run.Key, run.Unit.Id, snap.Bytes ?? [], StillHeld(run.Ownership)).ConfigureAwait(false);
        }
        catch (OwnershipLostException)
        {
            return PromotionResult<T>.Of(PromotionKind.OwnershipLost);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            // L stays as a copy; N outranks it and the next promotion or pass removes it.
            _ = ex;
        }

        return null;
    }

    private async ValueTask<PathRead<T>> ReadNativeAsync(string n, CancellationToken cancellationToken) =>
        Interpret(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), _p.Native);

    private async ValueTask<KeyMoveSnap> SourceSnapAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_source.IsAvailable)
        {
            return KeyMoveSnap.Of(ReadOutcome.Unavailable);
        }

        await At(SchedulePointStep.ReadSource).ConfigureAwait(false);
        return await _source.SnapshotAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private string? MarkerField(KeyMoveData data, string key) => _p.NoOrigin ? data.Names.MarkerField(_p.InPlaceKey(key)) : null;

    private ValueTask At(SchedulePointStep step) => _p.Schedule.AtAsync(step);

    private void Count(string counter) => RouterTelemetry.CountMove(counter, _p.CacheId, _p.Group);

    private readonly record struct PromotionRun(string Key, string N, FencedKeys Keys, MoverFlavor? Mover, AdmittedUnit Unit, KeyMoveData Data, int Attempt, IMoveOwnership Ownership);
}
