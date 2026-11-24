using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The per-key promotion (CAP, the snapshot of the source side, decode, encode, PUB, EXISTS, CMPDEL, and the capture-to-snapshot bound), shared by the read-through and the mover.</summary>
/// <remarks>A run with no <see cref="MoverFlavor"/> is the read-through. Forward, the source is L and the target N under G with an <c>nx</c> publish; reverse, the source is N and the target L under GL with a <c>set</c> publish, since N is the authority.</remarks>
internal sealed class KeyPromotion<T>
    where T : class
{
    private const CommandFlags Master = CommandFlags.DemandMaster;

    private readonly KeyMoveClientParts<T> _p;
    private readonly IMoveSide<T> _source;
    private readonly KeyMoveOptions _options;
    private readonly ISupersessionHandler? _readThrough;
    private readonly bool _reverse;
    private readonly IKeyMoveCodec<T> _target;

    /// <param name="readThrough">Told when a read-through's capture meets a live supersession marker; the mover's handler comes with its <see cref="MoverFlavor"/>.</param>
    /// <param name="reverse">The source is N, reached through <paramref name="source"/>, and the target is L.</param>
    public KeyPromotion(KeyMoveClientParts<T> parts, IMoveSide<T> source, ISupersessionHandler? readThrough = null, bool reverse = false)
    {
        _p = parts ?? throw new ArgumentNullException(nameof(parts));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = parts.Options;
        _readThrough = readThrough;
        _reverse = reverse;
        _target = reverse ? parts.InPlace : parts.Native;
    }

    private long FenceTtlMs => (long)_options.FenceTtl.TotalMilliseconds;

    // A cached null on the native side is the answer when that side caches nulls; on the in-place side it is a miss.
    public static PathRead<T> Interpret(KeyMoveSnap snap, IKeyMoveCodec<T> codec, bool authoritativeNull = false)
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
            DecodeClass.Null when authoritativeNull && codec.EncodeNull() is not null => new PathRead<T>(ReadOutcome.Absent, null, null) { Stops = true },
            DecodeClass.Expired or DecodeClass.Null => PathRead<T>.Of(ReadOutcome.Absent),
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
        var target = _reverse ? _p.InPlaceKey(key) : _p.NativeKey(key);
        var fence = _reverse ? data.Names.ReverseFence(target) : data.Names.ForwardFence(target);
        var run = new PromotionRun(key, target, new FencedKeys(fence, target, data.Names.ControlFor(_p.Group, target)), mover, unit, data, attempt, ownership);

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
        var (served, skipped) = await ReadSourceAsync(run, snap, cancellationToken).ConfigureAwait(false);
        var deleteStaleTarget = _reverse && mover is not null && skipped is { Kind: PromotionKind.TooLarge };
        if (skipped is { } skip && !deleteStaleTarget)
        {
            return skip;
        }

        var uncached = served with { Uncached = true };
        if (AbandonReason(data, events, started, replied, snap.TtlMs) is { } reason)
        {
            return new PromotionResult<T>(PromotionKind.Abandoned, uncached, Reason: reason);
        }

        if (deleteStaleTarget)
        {
            return await DeleteOversizedTargetAsync(run, capture, uncached, cancellationToken).ConfigureAwait(false);
        }

        byte[] encoded;
        try
        {
            encoded = served.Value is null ? _target.EncodeNull()! : _target.Encode(served.Value);
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
        return lost ?? new PromotionResult<T>(drained ? PromotionKind.Drained : PromotionKind.Promoted, served, Bytes: encoded.Length);
    }

    /// <summary>Classifies <paramref name="key"/> as the mover would meet it, reading L and asking N whether it holds the key, and writes and counts nothing.</summary>
    public async ValueTask<DryRunProbe> ProbeAsync(string key, MoverFlavor mover, bool decode, CancellationToken cancellationToken)
    {
        var snap = await SourceSnapAsync(key, cancellationToken).ConfigureAwait(false);
        if (snap.Kind != ReadOutcome.Found)
        {
            return new DryRunProbe(UnfoundClass(snap.Kind), null, null);
        }

        var probe = decode
            ? await ProbeDecodedAsync(key, mover, snap, cancellationToken).ConfigureAwait(false)
            : ProbeStored(key, mover, snap);
        if (probe.Class != DryRunClasses.Movable)
        {
            return probe;
        }

        var exists = await ExistsAsync(_p.NativeKey(key), SchedulePointStep.ConfirmDestination, cancellationToken).ConfigureAwait(false);
        return exists switch
        {
            true => probe with { Class = DryRunClasses.Duplicate, EncodedBytes = null },
            null => probe with { Class = DryRunClasses.Unavailable, EncodedBytes = null },
            false => probe,
        };
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

    private static bool IsShort(KeyMoveSnap snap, MoverFlavor mover) => snap.TtlMs >= 0 && snap.TtlMs < mover.MinTtl.TotalMilliseconds;

    private static string UnfoundClass(ReadOutcome kind) => kind switch
    {
        ReadOutcome.Absent => DryRunClasses.Absent,
        ReadOutcome.Unreadable => DryRunClasses.Unreadable,
        _ => DryRunClasses.Unavailable,
    };

    private static string SkippedClass(PromotionKind kind) => kind switch
    {
        PromotionKind.Expired => DryRunClasses.Expired,
        PromotionKind.TooLarge => DryRunClasses.TooLarge,
        PromotionKind.NullNotCached => DryRunClasses.NullNotCached,
        PromotionKind.Unreadable => DryRunClasses.Unreadable,
        PromotionKind.Unmappable => DryRunClasses.Unmappable,
        PromotionKind.ShortTtl => DryRunClasses.ShortTtl,
        _ => DryRunClasses.Unavailable,
    };

    private DryRunProbe ProbeStored(string key, MoverFlavor mover, KeyMoveSnap snap)
    {
        var bytes = snap.Bytes?.Length ?? 0;
        var classification = bytes switch
        {
            var length when length > _options.MaxValueBytes => DryRunClasses.TooLarge,
            _ when !_source.Maps(key) => DryRunClasses.Unmappable,
            _ when IsShort(snap, mover) => DryRunClasses.ShortTtl,
            _ => DryRunClasses.Movable,
        };

        return new DryRunProbe(classification, bytes, null);
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
        // In reverse an L that exists is an older copy, which the publish replaces.
        if (capture.DestinationExists && run.Mover is null && !_reverse)
        {
            return PromotionResult<T>.Of(PromotionKind.NativeExists, await ReadNativeAsync(run.Target, cancellationToken).ConfigureAwait(false));
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

    // Reads the snapshot of L into the value to serve, or the result that ends the run before publishing. A cached null serves as a miss and goes on as the other side's null.
    private async ValueTask<(PathRead<T> Served, PromotionResult<T>? Skipped)> ReadSourceAsync(PromotionRun run, KeyMoveSnap snap, CancellationToken cancellationToken)
    {
        if (snap.Kind != ReadOutcome.Found)
        {
            return (default, Unfound(snap.Kind));
        }

        var decoded = Decode(_source.Codec, snap);
        if (decoded.Class == DecodeClass.Unreadable ||
            decoded is { Class: not (DecodeClass.Expired or DecodeClass.Null), Value: null })
        {
            return (default, await UnreadableSourceAsync(run, snap, cancellationToken).ConfigureAwait(false));
        }

        if (decoded.Class == DecodeClass.Expired)
        {
            return (default, PromotionResult<T>.Of(PromotionKind.Expired, ReadOutcome.Absent));
        }

        // Only forward read-through can drop an oversized value or a null the target cannot store.
        var isNull = decoded.Class == DecodeClass.Null;
        var canPublishNull = isNull && _target.EncodeNull() is not null;
        var droppable = !_reverse && run.Mover is null && KeyMoveValueClasses.Droppable(decoded.Class, _p.NoOrigin, canPublishNull);
        if (isNull && !canPublishNull)
        {
            return (default, droppable
                ? await DroppedAsync(run, snap, PromotionResult<T>.Of(PromotionKind.SourceAbsent), cancellationToken).ConfigureAwait(false)
                : PromotionResult<T>.Of(PromotionKind.NullNotCached));
        }

        var expiration = snap.TtlMs < 0 ? (DateTimeOffset?)null : snap.At.AddMilliseconds(snap.TtlMs);
        var served = isNull ? PathRead<T>.Of(ReadOutcome.Absent) : new PathRead<T>(ReadOutcome.Found, decoded.Value, expiration);
        var unmoved = Unmoved(run, snap, decoded.Class, served);
        if (unmoved is { } stop && droppable)
        {
            unmoved = await DroppedAsync(run, snap, stop, cancellationToken).ConfigureAwait(false);
        }

        return (served, unmoved);
    }

    private PromotionResult<T> Unfound(ReadOutcome kind)
    {
        if (kind == ReadOutcome.Unreadable)
        {
            Count("unreadable_source");
        }

        if (kind == ReadOutcome.Absent)
        {
            return PromotionResult<T>.Of(PromotionKind.SourceAbsent);
        }

        return PromotionResult<T>.Of(kind == ReadOutcome.Unreadable ? PromotionKind.Unreadable : PromotionKind.Unavailable, kind);
    }

    private async ValueTask<PromotionResult<T>> UnreadableSourceAsync(PromotionRun run, KeyMoveSnap snap, CancellationToken cancellationToken)
    {
        if (!run.Quiet)
        {
            Count("unreadable_source");
        }

        var unreadable = PromotionResult<T>.Of(PromotionKind.Unreadable, ReadOutcome.Unreadable);

        // Forward read-through follows the inventory's length rule even when decoding fails.
        return !_reverse && run.Mover is null && (snap.Bytes?.Length ?? 0) > _options.MaxValueBytes && KeyMoveValueClasses.Droppable(DecodeClass.TooLarge, _p.NoOrigin, false)
            ? await DroppedAsync(run, snap, unreadable, cancellationToken).ConfigureAwait(false)
            : unreadable;
    }

    // The source copy is gone: what the run ends with, or that the owner lost it first.
    // A quiet run only reads, so it never drops L.
    private async ValueTask<PromotionResult<T>> DroppedAsync(PromotionRun run, KeyMoveSnap snap, PromotionResult<T> ended, CancellationToken cancellationToken) =>
        run.Quiet ? ended : await DropSourceAsync(run, snap, cancellationToken).ConfigureAwait(false) ?? ended;

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
        if (run.Mover is { } mover && IsShort(snap, mover))
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

    private Publication PublicationOf(Capture capture, long ttlMs)
    {
        var px = ttlMs < 0 ? MoveScripts.NoExpiry : ttlMs;
        return new Publication(capture.Token ?? throw new InvalidOperationException("A captured fence requires a token."), capture.Gate ?? throw new InvalidOperationException("A captured fence requires a gate."), _p.CacheId, MoveScripts.NewNonce(), FenceTtlMs, px, px == MoveScripts.NoExpiry ? MoveScripts.NoExpiry : capture.Tcap, !_reverse);
    }

    private async ValueTask<(bool Drained, PromotionResult<T>? Stopped)> PublishAsync(PromotionRun run, Capture capture, byte[] encoded, long ttlMs, PathRead<T> uncached, CancellationToken cancellationToken)
    {
        var publication = PublicationOf(capture, ttlMs);
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

        // The bytes went over the wire: they are recorded now, with no wait, and the runner pays them before the next key. A retry or an out-of-memory second try of the same key records nothing more.
        run.Ownership.Published(encoded.Length);
        return await PublishOutcomeAsync(run, published, uncached, cancellationToken).ConfigureAwait(false);
    }

    // The oversized native value stays until the release sweep. Its older legacy copy must be gone before the pass can count the skip.
    private async ValueTask<PromotionResult<T>> DeleteOversizedTargetAsync(PromotionRun run, Capture capture, PathRead<T> uncached, CancellationToken cancellationToken)
    {
        var publication = PublicationOf(capture, MoveScripts.NoExpiry);
        await At(SchedulePointStep.Publish).ConfigureAwait(false);
        if (!await run.Ownership.HoldsAsync(cancellationToken).ConfigureAwait(false))
        {
            return PromotionResult<T>.Of(PromotionKind.OwnershipLost);
        }

        long deleted;
        try
        {
            deleted = await run.Data.Scripts.DeleteCapturedAsync(run.Keys, run.Unit.Id, publication, StillHeld(run.Ownership)).ConfigureAwait(false);
        }
        catch (OwnershipLostException)
        {
            return PromotionResult<T>.Of(PromotionKind.OwnershipLost);
        }

        var (_, stopped) = await PublishOutcomeAsync(run, deleted, uncached, cancellationToken).ConfigureAwait(false);
        return stopped ?? PromotionResult<T>.Of(PromotionKind.TooLarge, uncached);
    }

    private async ValueTask<(bool Drained, PromotionResult<T>? Stopped)> PublishOutcomeAsync(PromotionRun run, long published, PathRead<T> uncached, CancellationToken cancellationToken)
    {
        switch (published)
        {
            case MoveScripts.Published:
                return (false, null);
            case MoveScripts.DestinationExists when run.Mover is null:
                var found = await ReadNativeAsync(run.Target, cancellationToken).ConfigureAwait(false);
                return (false, PromotionResult<T>.Of(PromotionKind.PublishFoundNative, found.Kind == ReadOutcome.Found || found.Stops ? found : uncached));
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

    // The source stays as the copy unless the target is there; the delete is a compare against the exact bytes read. Null unless ownership was lost.
    private async ValueTask<PromotionResult<T>?> DeleteSourceAsync(PromotionRun run, KeyMoveSnap snap, CancellationToken cancellationToken)
    {
        if (await ExistsAsync(run.Target, SchedulePointStep.ConfirmDestination, cancellationToken).ConfigureAwait(false) != true)
        {
            return null;
        }

        return await DropSourceAsync(run, snap, cancellationToken).ConfigureAwait(false);
    }

    // The delete is a compare against the exact bytes read: a key that changed since stays. Null unless ownership was lost.
    private async ValueTask<PromotionResult<T>?> DropSourceAsync(PromotionRun run, KeyMoveSnap snap, CancellationToken cancellationToken)
    {
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

    private async ValueTask<DryRunProbe> ProbeDecodedAsync(string key, MoverFlavor mover, KeyMoveSnap snap, CancellationToken cancellationToken)
    {
        // Only the mover and the key of the run are read on this path: it never fences, publishes or deletes.
        var run = new PromotionRun(key, _p.NativeKey(key), default, mover, null!, null!, 0, AlwaysOwned.Instance, Quiet: true);
        var bytes = snap.Bytes?.Length ?? 0;
        var (served, skipped) = await ReadSourceAsync(run, snap, cancellationToken).ConfigureAwait(false);
        if (skipped is { } stop)
        {
            return new DryRunProbe(SkippedClass(stop.Kind), bytes, null, Decoded: true);
        }

        try
        {
            var encoded = served.Value is null ? _p.Native.EncodeNull()! : _p.Native.Encode(served.Value);
            return new DryRunProbe(DryRunClasses.Movable, bytes, encoded.Length, Decoded: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new DryRunProbe(DryRunClasses.Unreadable, bytes, null, Decoded: true);
        }
    }

    private async ValueTask<PathRead<T>> ReadNativeAsync(string n, CancellationToken cancellationToken) =>
        Interpret(await SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), _p.Native, authoritativeNull: true);

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

    // The supersession markers belong to the forward NoOrigin writes.
    private string? MarkerField(KeyMoveData data, string key) => _p.NoOrigin && !_reverse ? data.Names.MarkerField(_p.InPlaceKey(key)) : null;

    private ValueTask At(SchedulePointStep step) => _p.Schedule.AtAsync(step);

    private void Count(string counter) => RouterTelemetry.CountMove(counter, _p.CacheId, _p.Group);

    private readonly record struct PromotionRun(string Key, string Target, FencedKeys Keys, MoverFlavor? Mover, AdmittedUnit Unit, KeyMoveData Data, int Attempt, IMoveOwnership Ownership, bool Quiet = false);
}
