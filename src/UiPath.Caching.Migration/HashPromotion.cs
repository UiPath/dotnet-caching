using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The forward promotion of one hash from L to N in S3 : the fields are staged in N's slot, published by one rename under the fence, and L is deleted by a compare of every field.</summary>
internal sealed class HashPromotion<T>(KeyMoveHashClientParts<T> parts, HashReads<T> reads, NegativeHints hints, ISupersessionHandler helper)
    where T : class
{
    private KeyMoveData? Data => parts.Plane.Data;

    private KeyMoveOptions Options => parts.Options;

    /// <summary>Promotes the hash of <paramref name="key"/> from L to N, and tells how it ended.</summary>
    public ValueTask<HashPromotionResult<T>> PromoteAsync(string key, HashPromotionFlavour flavour, CancellationToken cancellationToken = default) =>
        PromoteAsync(key, flavour, null, cancellationToken);

    public async ValueTask<HashPromotionResult<T>> PromoteAsync(string key, HashPromotionFlavour flavour, string[]? fields, CancellationToken cancellationToken)
    {
        try
        {
            var plan = KeyMovePlanning.PlanFor(parts.Plane, parts.Group, parts.CacheId, write: false);
            if (plan.Mode is not (KeyMoveMode.S3 or KeyMoveMode.S4))
            {
                return HashPromotionResult<T>.Of(HashPromotionStatus.Replan);
            }

            var attempt = 0;
            while (true)
            {
                var step = await PromoteOnceAsync(key, flavour, plan, attempt, fields, cancellationToken).ConfigureAwait(false);
                if (step.Status != HashPromotionStatus.Restart)
                {
                    return step;
                }

                attempt++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            return HashPromotionResult<T>.Of(HashPromotionStatus.Unavailable, ReadOutcome.Unavailable);
        }
    }

    private static HashPromotionResult<T> Done(HashRead<T> read) => HashPromotionResult<T>.Of(HashPromotionStatus.Complete, read);

    private static HashPromotionResult<T> Done(ReadOutcome kind) => HashPromotionResult<T>.Of(HashPromotionStatus.Complete, kind);

    private bool DropAllowed(KeyMovePlan plan) => plan.Mode is KeyMoveMode.S3 or KeyMoveMode.S4 && KeyMoveValueClasses.Droppable(DecodeClass.TooLarge, parts.NoOrigin, false);

    private void Count(string counter) => RouterTelemetry.CountMove(counter, parts.CacheId, parts.Group);

    private async ValueTask<HashPromotionResult<T>> PromoteOnceAsync(string key, HashPromotionFlavour flavour, KeyMovePlan plan, int attempt, string[]? fields, CancellationToken cancellationToken)
    {
        var l = parts.InPlaceKey(key);
        var n = parts.NativeKey(key);
        var identity = plan.Identity;
        var ensure = flavour == HashPromotionFlavour.Ensure;

        // A merge has probed L itself. A read goes N on a replica, N on the master, the hint, then the probe, so a genuine miss makes no fence.
        if (!ensure)
        {
            var replica = HashReads<T>.Interpret(await reads.SnapAsync(n, replica: true, SchedulePointStep.ReadDestination, cancellationToken).ConfigureAwait(false), parts.Native, authoritativeNull: true, fields: fields);
            if (!replica.FallsThrough)
            {
                return Done(replica);
            }

            var master = HashReads<T>.Interpret(await reads.SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), parts.Native, authoritativeNull: true, fields: fields);
            if (!master.FallsThrough)
            {
                return Done(master);
            }

            if (hints.Has(key, identity, MoveSide.Legacy))
            {
                return Done(ReadOutcome.Absent);
            }

            var probe = await reads.ExistsAsync(l, SchedulePointStep.ProbeSource, cancellationToken).ConfigureAwait(false);
            if (probe is null)
            {
                return HashPromotionResult<T>.Of(HashPromotionStatus.Unavailable, ReadOutcome.Unavailable);
            }

            if (probe == false)
            {
                return Done(await MissAsync(key, n, identity, fields, cancellationToken).ConfigureAwait(false));
            }
        }

        var unit = parts.Plane.Enter(parts.Group, true);
        if (unit is not null && plan.Act?.Id is { } expected && unit.Id != expected)
        {
            // A plan made under an older state is never run with a newer identity.
            unit.Dispose();
            return HashPromotionResult<T>.Of(HashPromotionStatus.Replan);
        }

        using (unit)
        {
            if (unit is null || Data is not { } data)
            {
                return await UnpromotedAsync(key, ensure, fields, cancellationToken).ConfigureAwait(false);
            }

            var pass = NewPass(key, flavour, plan, attempt, unit, data, fields);
            return await RunUnitAsync(pass, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<HashPromotionResult<T>> RunUnitAsync(PromotionPass pass, CancellationToken cancellationToken)
    {
        if (await CaptureSourceAsync(pass, cancellationToken).ConfigureAwait(false) is { } captured)
        {
            return captured;
        }

        if (await ReadSourceAsync(pass, cancellationToken).ConfigureAwait(false) is { } unread)
        {
            return unread;
        }

        if (await DecodeSourceAsync(pass, cancellationToken).ConfigureAwait(false) is { } undecoded)
        {
            return undecoded;
        }

        if (await StageFieldsAsync(pass, cancellationToken).ConfigureAwait(false) is { } unstaged)
        {
            return unstaged;
        }

        if (AbandonedPromotion(pass) is { } abandoned)
        {
            return abandoned;
        }

        if (await PublishAsync(pass, cancellationToken).ConfigureAwait(false) is { } unpublished)
        {
            return unpublished;
        }

        return await ConfirmPublishedAsync(pass, cancellationToken).ConfigureAwait(false);
    }

    private PromotionPass NewPass(string key, HashPromotionFlavour flavour, KeyMovePlan plan, int attempt, AdmittedUnit unit, KeyMoveData data, string[]? fields)
    {
        var l = parts.InPlaceKey(key);
        var n = parts.NativeKey(key);
        var keys = new FencedKeys(data.Names.ForwardFence(n), n, data.Names.ControlFor(parts.Group, n));
        var control = data.Names.ControlFor(parts.Group, l);
        return new PromotionPass(key, l, n, plan, flavour == HashPromotionFlavour.Ensure, attempt, unit, data, keys, control) { Fields = fields };
    }

    // The capture, never in parallel with the snapshot of L. The bound counts from before CAP is sent, so a delayed CAP reply counts.
    private async ValueTask<HashPromotionResult<T>?> CaptureSourceAsync(PromotionPass pass, CancellationToken cancellationToken)
    {
        var (data, unit) = (pass.Data, pass.Unit);
        pass.Events = data.ConnectionEvents();
        pass.Started = parts.Clock.GetTimestamp();
        await reads.At(SchedulePointStep.Capture).ConfigureAwait(false);
        var capture = await data.Scripts.CaptureAsync(pass.Keys, unit.Id, parts.CacheId, MoveScripts.NewNonce(), (long)Options.FenceTtl.TotalMilliseconds, parts.NoOrigin ? data.Names.MarkerField(pass.L) : null).ConfigureAwait(false);
        pass.Capture = capture;
        if (capture.Code == MoveScripts.IdentityMismatch)
        {
            return unit.Readmit() ? HashPromotionResult<T>.Of(HashPromotionStatus.Replan) : await UnpromotedAsync(pass.Key, pass.Ensure, pass.Fields, cancellationToken).ConfigureAwait(false);
        }

        if (capture.Code == MoveScripts.LiveSupersession)
        {
            // The copy of L is older than the newest write of N: it is deleted and the marker removed, and nothing is promoted.
            return await HelpAsync(pass, capture.Marker, cancellationToken).ConfigureAwait(false);
        }

        if (!capture.Captured)
        {
            // The cache gate is closed (a Clear is running) or absent: a miss with no hint.
            return HashPromotionResult<T>.Of(HashPromotionStatus.NotPromoted);
        }

        return capture.DestinationExists ? await ExistingAsync(pass, null, cancellationToken).ConfigureAwait(false) : null;
    }

    // L, after the capture. A promotion does not read a hash above the field cap; a cache with no origin then serves it with an uncapped union read, uncached.
    private async ValueTask<HashPromotionResult<T>?> ReadSourceAsync(PromotionPass pass, CancellationToken cancellationToken)
    {
        var snap = await reads.SnapAsync(pass.L, replica: false, SchedulePointStep.ReadSource, cancellationToken, parts.MaxHashFields).ConfigureAwait(false);
        pass.Snap = snap;
        pass.Replied = parts.Clock.GetTimestamp();
        if (snap.Kind == ReadOutcome.Absent)
        {
            return await AbsentSourceAsync(pass, cancellationToken).ConfigureAwait(false);
        }

        if (snap.Kind == ReadOutcome.Unreadable)
        {
            return UnreadableSource(snap, pass.Fields);
        }

        if (snap.Kind != ReadOutcome.Found)
        {
            return HashPromotionResult<T>.Of(HashPromotionStatus.Unavailable, snap.Kind);
        }

        return snap.TooLarge ? await OverCapAsync(pass, null, cancellationToken).ConfigureAwait(false) : null;
    }

    private async ValueTask<HashPromotionResult<T>> AbsentSourceAsync(PromotionPass pass, CancellationToken cancellationToken) =>
        pass.Ensure ? Done(ReadOutcome.Absent) : Done(await MissAsync(pass.Key, pass.N, pass.Plan.Identity, pass.Fields, cancellationToken).ConfigureAwait(false));

    private HashPromotionResult<T> UnreadableSource(HashSnap snap, string[]? fields)
    {
        var read = HashReads<T>.RecoverFields(snap, parts.InPlace, fields);
        if (read.Kind == ReadOutcome.Unreadable)
        {
            Count("unreadable_source");
        }

        return HashPromotionResult<T>.Of(HashPromotionStatus.Unreadable, read);
    }

    private async ValueTask<HashPromotionResult<T>?> DecodeSourceAsync(PromotionPass pass, CancellationToken cancellationToken)
    {
        var snap = pass.Snap;
        var decoded = DecodeSnap(snap);
        pass.Decoded = decoded;
        if (decoded.Class == DecodeClass.Unreadable)
        {
            return UnreadableSource(snap, pass.Fields);
        }

        pass.Expiration = snap.TtlMs < 0 ? null : snap.At.AddMilliseconds(snap.TtlMs);
        if (decoded.Class != DecodeClass.TooLarge)
        {
            return null;
        }

        HashRead<T>? held = decoded.Fields.Count == 0 ? null : new HashRead<T>(ReadOutcome.Found, new Dictionary<string, T?>(decoded.Fields, StringComparer.Ordinal), pass.Expiration);
        return await OverCapAsync(pass, held, cancellationToken).ConfigureAwait(false);
    }

    // A field name of the reserved pattern cannot be told from a system field once it is promoted, so it makes the hash unmappable; the system fields of a library format that carries them (the metadata) are the format's own, and are kept.
    private DecodedHash<T> DecodeSnap(HashSnap snap)
    {
        try
        {
            return snap.Entries!.Any(e => KnownFieldNames.IsReserved(e.Name.ToString()) && !(parts.InPlace.CarriesMetadata && KnownFieldNames.IsSystemField(e.Name.ToString()))) ? DecodedHash<T>.Of(DecodeClass.Unreadable, snap.Entries!.Length) : parts.InPlace.DecodeFields(snap.Stored());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DecodedHash<T>.Of(DecodeClass.Unreadable, snap.Entries!.Length);
        }
    }

    // A field that is a cached null stays one where N caches nulls and is left out where it does not; a hash with nothing left to store is one the codec refuses for good, and is dropped like a null value of a string.
    private async ValueTask<HashPromotionResult<T>?> StageFieldsAsync(PromotionPass pass, CancellationToken cancellationToken)
    {
        var decoded = pass.Decoded;
        var fields = new Dictionary<string, T?>(decoded.Fields.Where(f => f.Value is not null || parts.Native.CachesNulls), StringComparer.Ordinal);
        pass.Served = fields.Count == 0 ? HashRead<T>.Of(ReadOutcome.Absent) : new HashRead<T>(ReadOutcome.Found, new Dictionary<string, T?>(decoded.Fields, StringComparer.Ordinal), pass.Expiration, decoded.Metadata);
        if (!TryEncodeStaged(decoded, fields, out var staged))
        {
            return HashPromotionResult<T>.Of(HashPromotionStatus.NotPromoted, pass.Uncached);
        }

        pass.Staged = staged;
        if (staged.Count == 0)
        {
            await DropWholeAsync(pass.L, pass.Control, pass.Data, pass.Unit.Id, pass.Snap.Entries!).ConfigureAwait(false);
            return await AbsentSourceAsync(pass, cancellationToken).ConfigureAwait(false);
        }

        return parts.Mappable?.Invoke(pass.Key) == false ? HashPromotionResult<T>.Of(HashPromotionStatus.Unreadable, pass.Served) : null;
    }

    private bool TryEncodeStaged(DecodedHash<T> decoded, Dictionary<string, T?> fields, out IReadOnlyList<KeyValuePair<string, byte[]>> staged)
    {
        try
        {
            staged = fields.Count == 0 && decoded.Fields.Count > 0 ? [] : parts.Native.EncodeFields(fields, decoded.Metadata);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            staged = [];
            return false;
        }
    }

    // Abandon, serving the decoded hash uncached.
    private HashPromotionResult<T>? AbandonedPromotion(PromotionPass pass)
    {
        if (parts.Clock.GetElapsedTime(pass.Started, pass.Replied) > Options.CaptureToSnapshotMax)
        {
            Count("capture_slow");
            return HashPromotionResult<T>.Of(HashPromotionStatus.NotPromoted, pass.Uncached);
        }

        if (parts.Clock.GetElapsedTime(pass.Started) > Options.PromotionBudget || pass.Data.ConnectionEvents() != pass.Events)
        {
            Count("promotion_abandoned");
            return HashPromotionResult<T>.Of(HashPromotionStatus.NotPromoted, pass.Uncached);
        }

        // A life that is a count below 1 ms is not published; none is published as none.
        return pass.Snap.TtlMs is >= 0 and < 1 ? HashPromotionResult<T>.Of(HashPromotionStatus.NotPromoted, pass.Uncached) : null;
    }

    private async ValueTask<HashPromotionResult<T>?> PublishAsync(PromotionPass pass, CancellationToken cancellationToken)
    {
        if (await PublishStagedAsync(pass).ConfigureAwait(false) is not { } published)
        {
            return HashPromotionResult<T>.Of(HashPromotionStatus.NotPromoted, pass.Uncached);
        }

        return await JudgePublishedAsync(pass, published, cancellationToken).ConfigureAwait(false);
    }

    // The fields are staged in N's slot and renamed over N as they are, so the expiry is the source's px and no version is minted. Null when the staging or the publish failed.
    private async ValueTask<long?> PublishStagedAsync(PromotionPass pass)
    {
        var (data, snap, capture) = (pass.Data, pass.Snap, pass.Capture);
        var staging = data.Names.ForwardStaging(pass.N, MoveScripts.NewNonce());
        var px = snap.TtlMs < 0 ? MoveScripts.NoExpiry : snap.TtlMs;
        var publication = new Publication(capture.Token!, capture.Gate!, parts.CacheId, MoveScripts.NewNonce(), (long)Options.FenceTtl.TotalMilliseconds, px, px == MoveScripts.NoExpiry ? MoveScripts.NoExpiry : capture.Tcap, true);
        try
        {
            await reads.At(SchedulePointStep.StageHash).ConfigureAwait(false);
            if (!await HashStaging.WriteAsync(data, staging, pass.Staged, Options.FenceTtl).ConfigureAwait(false))
            {
                await HashStaging.DeleteAsync(data, staging).ConfigureAwait(false);
                return null;
            }

            await reads.At(SchedulePointStep.Publish).ConfigureAwait(false);
            return await data.Scripts.PublishHashAsync(pass.Keys, staging, pass.Unit.Id, publication).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            await HashStaging.DeleteAsync(data, staging).ConfigureAwait(false);
            return null;
        }
    }

    // The publish reply: only a published hash goes on to the delete of L.
    private async ValueTask<HashPromotionResult<T>?> JudgePublishedAsync(PromotionPass pass, long published, CancellationToken cancellationToken)
    {
        switch (published)
        {
            case MoveScripts.Published:
                return null;
            case MoveScripts.DestinationExists:
                return await ExistingAsync(pass, pass.Uncached, cancellationToken).ConfigureAwait(false);
            case MoveScripts.FenceMoved:
                if (pass.Attempt < Options.MaxFenceRetries)
                {
                    // A mutation happened: start again from the read of N.
                    return HashPromotionResult<T>.Of(HashPromotionStatus.Restart);
                }

                Count("fence_refused");
                return HashPromotionResult<T>.Of(HashPromotionStatus.NotPromoted, pass.Uncached);
            case MoveScripts.IdentityMismatch:
                return pass.Unit.Readmit() ? HashPromotionResult<T>.Of(HashPromotionStatus.Replan) : HashPromotionResult<T>.Of(HashPromotionStatus.NotPromoted, pass.Uncached);
            case MoveScripts.PastDeadline:
                Count("promotion_expiring");
                return HashPromotionResult<T>.Of(HashPromotionStatus.NotPromoted, pass.Uncached);
            default:
                return HashPromotionResult<T>.Of(HashPromotionStatus.NotPromoted, pass.Uncached);
        }
    }

    // L stays as the copy unless N is there; the delete is a compare of every field as read.
    private async ValueTask<HashPromotionResult<T>> ConfirmPublishedAsync(PromotionPass pass, CancellationToken cancellationToken)
    {
        var gone = false;
        if (await reads.ExistsAsync(pass.N, SchedulePointStep.ConfirmDestination, cancellationToken).ConfigureAwait(false) == true)
        {
            gone = await CompareDeleteAsync(pass.L, pass.Control, pass.Data, pass.Unit.Id, pass.Snap.Entries!, cancellationToken).ConfigureAwait(false);
        }

        // No event.
        return HashPromotionResult<T>.Of(gone ? HashPromotionStatus.Complete : HashPromotionStatus.Changing, pass.Served);
    }

    // The compare against every field as read: a hash that changed since stays; the result tells whether L is gone, which a compare that lost to a delete also is.
    private async ValueTask<bool> CompareDeleteAsync(string l, RedisKey control, KeyMoveData data, string unitId, HashEntry[] entries, CancellationToken cancellationToken)
    {
        await reads.At(SchedulePointStep.CompareDelete).ConfigureAwait(false);
        try
        {
            if (await data.Scripts.CompareDeleteHashAsync(l, control, unitId, entries).ConfigureAwait(false) == 1)
            {
                return true;
            }

            return await reads.ExistsAsync(l, SchedulePointStep.ProbeSource, cancellationToken).ConfigureAwait(false) == false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private async ValueTask DropWholeAsync(string l, RedisKey control, KeyMoveData data, string unitId, HashEntry[] entries)
    {
        await reads.At(SchedulePointStep.CompareDelete).ConfigureAwait(false);
        try
        {
            _ = await data.Scripts.CompareDeleteHashAsync(l, control, unitId, entries).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            _ = ex;
        }
    }

    // Above the cap the hash is not promoted. A cache with an origin drops it by an unconditional delete of L (a compare would have to send more than the cap), which is safe because L has no writer but deletes once S3 has begun; a cache without one keeps it, and it is served from where it is.
    private async ValueTask<HashPromotionResult<T>> OverCapAsync(PromotionPass pass, HashRead<T>? served, CancellationToken cancellationToken)
    {
        var dropped = false;
        if (!pass.Ensure && DropAllowed(pass.Plan))
        {
            await reads.At(SchedulePointStep.SourceDelete).ConfigureAwait(false);
            try
            {
                dropped = await pass.Data.Scripts.SourceDeleteAsync(pass.L, pass.Control, pass.Unit.Id).ConfigureAwait(false) >= 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
            {
                _ = ex;
            }
        }

        if (pass.Ensure)
        {
            return HashPromotionResult<T>.Of(HashPromotionStatus.TooLarge);
        }

        if (served is { } read)
        {
            return HashPromotionResult<T>.Of(HashPromotionStatus.TooLarge, read);
        }

        // The next read reloads a dropped hash from the origin; a kept one is read whole from where it is.
        return dropped
            ? HashPromotionResult<T>.Of(HashPromotionStatus.TooLarge)
            : HashPromotionResult<T>.Of(HashPromotionStatus.TooLarge, (await reads.ReadUnionAsync(pass.Key, withSource: true, pass.Fields, cancellationToken).ConfigureAwait(false)).Read with { Uncached = true });
    }

    private async ValueTask<HashPromotionResult<T>> ExistingAsync(PromotionPass pass, HashRead<T>? uncached, CancellationToken cancellationToken)
    {
        if (pass.Ensure)
        {
            return await reads.ExistsAsync(pass.L, SchedulePointStep.ProbeSource, cancellationToken).ConfigureAwait(false) switch
            {
                false => Done(ReadOutcome.Absent),
                null => HashPromotionResult<T>.Of(HashPromotionStatus.Unavailable, ReadOutcome.Unavailable),
                _ => HashPromotionResult<T>.Of(HashPromotionStatus.Changing),
            };
        }

        // Pick the side using the whole-hash outcome before considering selected fields.
        var snap = await reads.SnapAsync(pass.N, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false);
        var existing = HashReads<T>.Interpret(snap, parts.Native, authoritativeNull: true);
        if (existing.Kind != ReadOutcome.Found && !existing.Stops && uncached is { } fallback)
        {
            return HashPromotionResult<T>.Of(HashPromotionStatus.NotPromoted, fallback);
        }

        return Done(existing.Kind == ReadOutcome.Unreadable ? HashReads<T>.RecoverFields(snap, parts.Native, pass.Fields) : existing);
    }

    private async ValueTask<HashPromotionResult<T>> HelpAsync(PromotionPass pass, RedisValue marker, CancellationToken cancellationToken)
    {
        switch (await helper.OnSupersededAsync(new SupersededKey(parts.Group, parts.CacheId, pass.Key, marker, pass.Unit.Id), cancellationToken).ConfigureAwait(false))
        {
            case SupersessionAction.Retry:
                return pass.Ensure ? Done(ReadOutcome.Absent) : Done(await MissAsync(pass.Key, pass.N, pass.Plan.Identity, pass.Fields, cancellationToken).ConfigureAwait(false));
            case SupersessionAction.Rerun:
                return pass.Unit.Readmit() ? HashPromotionResult<T>.Of(HashPromotionStatus.Replan) : await UnpromotedAsync(pass.Key, pass.Ensure, pass.Fields, cancellationToken).ConfigureAwait(false);
            default:
                return HashPromotionResult<T>.Of(HashPromotionStatus.Unavailable, ReadOutcome.Unavailable);
        }
    }

    // The non-promoting union read, N first and L, then N on the master again; what it finds is served and not kept, as an abandoned promotion's hash is.
    private async ValueTask<HashPromotionResult<T>> UnpromotedAsync(string key, bool ensure, string[]? fields, CancellationToken cancellationToken)
    {
        if (ensure)
        {
            return HashPromotionResult<T>.Of(HashPromotionStatus.Unavailable, ReadOutcome.Unavailable);
        }

        var union = (await reads.ReadUnionAsync(key, withSource: true, fields, cancellationToken).ConfigureAwait(false)).Read;
        return HashPromotionResult<T>.Of(HashPromotionStatus.NotPromoted, union.Kind == ReadOutcome.Found ? union with { Uncached = true } : union);
    }

    // The miss path of steps 5 and 7: N is read on the master once more, and only an N that is absent again sets the hint.
    private async ValueTask<HashRead<T>> MissAsync(string key, string n, string identity, string[]? fields, CancellationToken cancellationToken)
    {
        var again = HashReads<T>.Interpret(await reads.SnapAsync(n, replica: false, SchedulePointStep.ReadDestinationOnMaster, cancellationToken).ConfigureAwait(false), parts.Native, authoritativeNull: true, fields: fields);
        if (again.FallsThrough)
        {
            hints.Set(key, identity, MoveSide.Legacy);
        }

        return again;
    }

    private sealed class PromotionPass(string key, string l, string n, KeyMovePlan plan, bool ensure, int attempt, AdmittedUnit unit, KeyMoveData data, FencedKeys keys, RedisKey control)
    {
        public string Key => key;

        public string L => l;

        public string N => n;

        public KeyMovePlan Plan => plan;

        public bool Ensure => ensure;

        public int Attempt => attempt;

        public string[]? Fields { get; init; }

        public AdmittedUnit Unit => unit;

        public KeyMoveData Data => data;

        public FencedKeys Keys => keys;

        public RedisKey Control => control;

        public Capture Capture { get; set; }

        public HashSnap Snap { get; set; }

        public DecodedHash<T> Decoded { get; set; }

        public DateTimeOffset? Expiration { get; set; }

        public HashRead<T> Served { get; set; }

        public IReadOnlyList<KeyValuePair<string, byte[]>> Staged { get; set; } = [];

        public long Started { get; set; }

        public long Replied { get; set; }

        public long Events { get; set; }

        public HashRead<T> Uncached => Served with { Uncached = true };
    }
}
