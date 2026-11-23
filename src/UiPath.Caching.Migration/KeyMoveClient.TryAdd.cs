using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveClient<T>
{
    public static NotSupportedException Refused(string what) => new($"{what} has no checked form while the group's keys move; it is available again in the library's own formats.");

    ValueTask<TryAddResult<T>> IPathClient<T>.TryAddAsync(string key, T? value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        if (value is not null)
        {
            var lifetime = deadline is { } at ? at - _p.Clock.GetUtcNow() : (TimeSpan?)null;
            return TryAddAsync(key, value, lifetime, cancellationToken);
        }

        return Moving
            ? ValueTask.FromResult(TryAddResult<T>.Failed(WriteOutcome.NotSent))
            : _p.Plain.TryAddAsync(key, null, deadline, cancellationToken);
    }

    /// <summary>The library's swap, refused while the keys move.</summary>
    public ValueTask<SwapResult<T>> SetIfChangedAsync(string key, T? value, IEqualityComparer<T> comparer, DateTimeOffset? deadline, CancellationToken cancellationToken) =>
        Moving ? throw Refused("A compare-and-swap") : _p.Plain.SetIfChangedAsync(key, value, comparer, deadline, cancellationToken);

    /// <summary>A refresh runs where the key lives: through the library when no key move applies, and refused while the keys move.</summary>
    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken) =>
        Moving ? throw Refused("A refresh") : _p.Plain.RefreshAsync(key, expiry, cancellationToken);

    /// <summary>The library's fill; while the keys move, the admitted fill of a managed state, and none where writes are plain.</summary>
    public async ValueTask<FillResult<T>> TryFillAsync(string key, T? value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        if (!Moving)
        {
            return await _p.Plain.TryFillAsync(key, value, deadline, cancellationToken).ConfigureAwait(false);
        }

        if (value is null)
        {
            return default;
        }

        // Plain writes land on the in-place key while the reads prefer the native one, and the move can advance between the read and the write: no admitted form exists, so the fill is refused.
        if (PlanFor(write: true).Mode == KeyMoveMode.Plain)
        {
            return default;
        }

        var box = new FillBox<T>();
        var written = await FillAsync(key, value, deadline, box, cancellationToken).ConfigureAwait(false);
        return written.Outcome switch
        {
            WriteOutcome.Committed => new FillResult<T>(FillOutcome.Added, null, deadline),
            _ when box.Lost && box.Stored is { } stored => new FillResult<T>(FillOutcome.Existed, stored, box.StoredExpiration),
            _ when box.Lost && box.Unreadable => new FillResult<T>(FillOutcome.Unreadable, null),
            _ => default,
        };
    }

    public ValueTask<bool?> ContainsAsync(string key, CancellationToken cancellationToken) => Moving ? ValueTask.FromResult<bool?>(null) : _p.Plain.ContainsAsync(key, cancellationToken);

    /// <summary>The remaining life: the library's own read when no key move applies, otherwise a PTTL on the side the reads use.</summary>
    public async ValueTask<TimeSpan?> TimeToLiveAsync(string key, CancellationToken cancellationToken)
    {
        if (!Moving)
        {
            return await _p.Plain.TimeToLiveAsync(key, cancellationToken).ConfigureAwait(false);
        }

        var expiration = await ExpireTimeAsync(key, cancellationToken).ConfigureAwait(false);
        return expiration is { } at ? at - _p.Clock.GetUtcNow() : null;
    }

    public async ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken cancellationToken)
    {
        if (!Moving)
        {
            return await _p.Plain.ExpireTimeAsync(key, cancellationToken).ConfigureAwait(false);
        }

        // The sides the read plan reads, in its order: S5 only N, S1 and S2 only L, otherwise N, L, then N again. The life alone is asked for, so nothing is decoded or promoted.
        var data = Data;
        if (data is null || !data.IsConnected())
        {
            throw new CacheUnavailableException($"Cache '{_p.CacheId}' could not answer a read of the expiry.");
        }

        try
        {
            foreach (var onSource in ExpirySides())
            {
                if (onSource)
                {
                    var snap = await Source.SnapshotAsync(key, cancellationToken).ConfigureAwait(false);
                    if (snap.Kind == ReadOutcome.Found)
                    {
                        return snap.TtlMs < 0 ? null : ExpirationAfter(snap.TtlMs);
                    }

                    continue;
                }

                var life = await data.Db.KeyTimeToLiveAsync(Key(Native(key)), Master).ConfigureAwait(false);
                if (life is { } span)
                {
                    return ExpirationAfter((long)Math.Ceiling(span.TotalMilliseconds));
                }

                if (await data.Db.KeyExistsAsync(Key(Native(key)), Master).ConfigureAwait(false))
                {
                    return null;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new CacheUnavailableException($"Cache '{_p.CacheId}' could not answer a read of the expiry.", ex);
        }
    }

    /// <summary>Creates an absent key using the write form for the current state, after checking both sides.</summary>
    /// <param name="lifetime">The claim's relative life; null for none. A create is new data, so its life is relative to the create.</param>
    public async ValueTask<TryAddResult<T>> TryAddAsync(string key, T value, TimeSpan? lifetime, CancellationToken cancellationToken)
    {
        var px = lifetime is { } span ? (long)Math.Ceiling(span.TotalMilliseconds) : MoveScripts.NoExpiry;
        if (px is < 1 and not MoveScripts.NoExpiry)
        {
            return TryAddResult<T>.Failed(WriteOutcome.NotSent);
        }

        var plan = PlanFor(write: true);
        var run = 0;
        while (true)
        {
            plan = await SettleWaitAsync(plan, cancellationToken).ConfigureAwait(false);
            if (plan.Mode == KeyMoveMode.Plain)
            {
                return await PlainCreateAsync(key, value, px, cancellationToken).ConfigureAwait(false);
            }

            // S2 closes NoOrigin and AtMostOnce caches, and a reverse state closes every create.
            if (CreateIsClosed(plan.Mode))
            {
                throw Unavailable();
            }

            var result = await TryAddOnceAsync(key, value, px, plan, cancellationToken).ConfigureAwait(false);
            if (result.Kind != TryAddKind.Rerun)
            {
                return result;
            }

            // The identity changed under the call: it is built again under the new one.
            Count(IdentityRerun);
            await Task.Delay(_options.AdmissionPoll, _p.Clock, cancellationToken).ConfigureAwait(false);
            if (run++ >= _options.MaxReruns)
            {
                return TryAddResult<T>.Failed(WriteOutcome.NotSent);
            }

            plan = PlanFor(write: true);
        }
    }

    async ValueTask<AddResult> IPathClient<T>.TryCreateAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        if (PlanFor(write: true).Mode == KeyMoveMode.Plain)
        {
            return await _p.Plain.TryCreateAsync(key, value, deadline, cancellationToken).ConfigureAwait(false);
        }

        TimeSpan? lifetime = deadline - _p.Clock.GetUtcNow();
        if (lifetime <= TimeSpan.Zero)
        {
            return AddOutcome.NotSent;
        }

        var result = await TryAddAsync(key, value, lifetime, cancellationToken).ConfigureAwait(false);
        return ResultOf(result);
    }

    private static AddResult ResultOf(TryAddResult<T> result) => result switch
    {
        { Kind: TryAddKind.Created } => AddOutcome.Added,
        { Kind: TryAddKind.Exists } => AddOutcome.Exists,
        { Failure: { Outcome: WriteOutcome.Unknown, SettleWithin: { } window } } => AddResult.UnknownWithin(window),
        { Failure: { Outcome: WriteOutcome.Unknown } } => AddOutcome.Unknown,
        _ => AddOutcome.NotSent,
    };

    private static TryAddResult<T> CreateOutcome(long code, AdmittedUnit unit) => code switch
    {
        MoveScripts.IdentityMismatch when unit.Readmit() => TryAddResult<T>.Rerun,
        MoveScripts.Published => TryAddResult<T>.Created,
        MoveScripts.DestinationExists => TryAddResult<T>.Exists(null),
        _ => TryAddResult<T>.Failed(WriteOutcome.NotSent),
    };

    private DateTimeOffset? ExpirationAfter(long milliseconds) =>
        milliseconds < 0 ? null : _p.Clock.GetUtcNow().AddMilliseconds(milliseconds);

    // Ask for expiry without decoding or promoting a value.
    // True for L, read through the source side; false for N.
    private bool[] ExpirySides() => PlanFor(write: false).Mode switch
    {
        KeyMoveMode.S5 => [false],
        KeyMoveMode.S1 or KeyMoveMode.S2 => [true],
        _ => [false, true, false],
    };

    private async ValueTask<TryAddResult<T>> PlainCreateAsync(string key, T value, long px, CancellationToken cancellationToken)
    {
        if (!Source.IsAvailable)
        {
            return TryAddResult<T>.Failed(WriteOutcome.NotSent);
        }

        try
        {
            var bytes = Source.Codec.Encode(value);
            var created = await Source.CreateAsync(key, bytes, px).ConfigureAwait(false);
            return created ? TryAddResult<T>.Created : TryAddResult<T>.Exists(null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return TryAddResult<T>.Failed(Typed(WriteOutcome.Unknown));
        }
    }

    private async ValueTask<TryAddResult<T>> TryAddOnceAsync(string key, T value, long px, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        // The union check first: a value in either side is the answer, and a value found in Src is promoted by the read.
        var existing = await ReadForCreateAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing.Kind != ReadOutcome.Absent)
        {
            return existing.Kind == ReadOutcome.Found ? TryAddResult<T>.Exists(existing.Value) : TryAddResult<T>.Failed(WriteOutcome.NotSent);
        }

        var (admitted, replan) = await AdmitAsync(promotion: false, plan.Act?.Id, cancellationToken).ConfigureAwait(false);
        if (replan)
        {
            return TryAddResult<T>.Rerun;
        }

        using var unit = admitted ?? throw Unavailable();
        if (Data is not { } data || !data.IsConnected() || SourceUnavailable(plan.Mode is not (KeyMoveMode.S4 or KeyMoveMode.S5)))
        {
            return TryAddResult<T>.Failed(WriteOutcome.NotSent);
        }

        var n = Native(key);
        try
        {
            var mode = plan.Mode;
            if (mode is KeyMoveMode.S1 or KeyMoveMode.S2 or KeyMoveMode.S4 or KeyMoveMode.S5)
            {
                await At(FillPoint(mode)).ConfigureAwait(false);
            }

            var code = mode switch
            {
                KeyMoveMode.S1 or KeyMoveMode.S2 => await Source.SetAsync(key, unit.Id, Source.Codec.Encode(value), px, true).ConfigureAwait(false),
                KeyMoveMode.S5 => CreatedCode(await data.Db.StringSetAsync(Key(n), _p.Native.Encode(value), Expiry(px), When.NotExists, Master).ConfigureAwait(false)),
                KeyMoveMode.S4 => await data.Scripts.FillAsync(Fenced(data, n), unit.Id, _p.Native.Encode(value), px, MoveScripts.NewNonce(), FenceTtlMs).ConfigureAwait(false),
                _ => await CaptureAndPublishAsync(data, n, key, value, px, unit, cancellationToken).ConfigureAwait(false),
            };
            return CreateOutcome(code, unit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return TryAddResult<T>.Failed(Typed(WriteOutcome.Unknown));
        }
    }

    private ValueTask<PathRead<T>> ReadForCreateAsync(string key, CancellationToken cancellationToken)
    {
        var plan = PlanFor(write: false);
        return plan.Mode == KeyMoveMode.Plain ? _p.Plain.ReadAsync(key, cancellationToken) : ReadManagedAsync(key, plan, cancellationToken);
    }

    // In S3, capture the destination fence, check for an existing key on both masters, and recheck N after probing L. Then publish with NX and a relative TTL (tcap -1, no deadline, no -3).
    private async ValueTask<long> CaptureAndPublishAsync(KeyMoveData data, string n, string key, T value, long px, AdmittedUnit unit, CancellationToken cancellationToken)
    {
        var keys = Fenced(data, n);
        for (var attempt = 0; attempt <= _options.MaxFenceRetries; attempt++)
        {
            await At(SchedulePointStep.Capture).ConfigureAwait(false);
            var capture = await data.Scripts.CaptureAsync(keys, unit.Id, _p.CacheId, MoveScripts.NewNonce(), FenceTtlMs).ConfigureAwait(false);
            if (capture.Code == MoveScripts.IdentityMismatch)
            {
                return MoveScripts.IdentityMismatch;
            }

            if (!capture.Captured)
            {
                throw Unavailable();
            }

            if (capture.DestinationExists || await SourceExistsAsync(key, SchedulePointStep.ProbeSource, cancellationToken).ConfigureAwait(false) != false)
            {
                // Something exists now: the claim is not ours. A value still in Src is promoted by the next read.
                return MoveScripts.DestinationExists;
            }

            if (await ExistsAsync(n, SchedulePointStep.ConfirmDestination, cancellationToken).ConfigureAwait(false) != false)
            {
                return MoveScripts.DestinationExists;
            }

            var publication = PublicationFor(capture, px, MoveScripts.NoExpiry);
            await At(SchedulePointStep.Publish).ConfigureAwait(false);
            var code = await data.Scripts.PublishAsync(keys, unit.Id, _p.Native.Encode(value), publication).ConfigureAwait(false);
            if (code != MoveScripts.FenceMoved)
            {
                return code;
            }

            // The fence moved: a mutation happened, so the capture and the union check run again.
        }

        return MoveScripts.FenceMoved;
    }

}
