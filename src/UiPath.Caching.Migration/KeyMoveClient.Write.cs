using System.Text;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveClient<T>
{
    public ValueTask<WriteResult> SetAsync(string key, T value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        var plan = PlanFor(write: true);
        return plan.Mode == KeyMoveMode.Plain ? _p.Plain.SetAsync(key, value, deadline, cancellationToken) : WriteManagedAsync(key, value, deadline, plan, cancellationToken);
    }

    public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        var plan = PlanFor(write: true);
        return plan.Mode == KeyMoveMode.Plain ? _p.Plain.RemoveAsync(key, cancellationToken) : WriteManagedAsync(key, null, null, plan, cancellationToken);
    }

    /// <summary>The ordered write steps for a state, or null when the cache is closed.</summary>
    internal WriteStep[]? StepsFor(KeyMovePlan plan, bool delete)
    {
        return plan.Mode switch
        {
            KeyMoveMode.S1 => [SourceStep(delete)],
            KeyMoveMode.S2 when !_p.NoOrigin => [SourceStep(delete)],
            KeyMoveMode.S3 or KeyMoveMode.S4 => DualWriteSteps(delete),
            KeyMoveMode.S5 => NativeWriteSteps(delete, plan.Act is { Swept: true }),
            _ => null,
        };
    }

    private static WriteStep SourceStep(bool delete) => delete ? WriteStep.SrcDel : WriteStep.SrcSet;

    private static WriteStep[] NativeWriteSteps(bool delete, bool swept)
    {
        var native = delete ? WriteStep.NativeDel : WriteStep.NativeSet;
        return swept ? [native] : [WriteStep.SrcDel, native];
    }

    private static SchedulePointStep Point(WriteStep step) => step switch
    {
        WriteStep.SrcSet => SchedulePointStep.SourceSet,
        WriteStep.SrcDel => SchedulePointStep.SourceDelete,
        WriteStep.BumpSet or WriteStep.BumpSetMarked or WriteStep.BumpDel => SchedulePointStep.Bump,
        _ => SchedulePointStep.NativeWrite,
    };

    private static long BooleanCode(bool result) => result ? 1 : 0;

    private static TimeSpan? Expiry(long px) => px < 0 ? null : TimeSpan.FromMilliseconds(px);

    private static InvalidOperationException MissingWriteValue() => new("A set step requires an encoded value.");

    private static bool ExpiredSet(WriteStep step, long px, bool boundedRetry) =>
        boundedRetry && px < 1 && step is WriteStep.SrcSet or WriteStep.BumpSet or WriteStep.BumpSetMarked or WriteStep.NativeSet;

    private WriteStep[] DualWriteSteps(bool delete)
    {
        if (delete)
        {
            return [WriteStep.SrcDel, WriteStep.BumpDel];
        }

        // NoOrigin writes N first to avoid a miss between the steps, and the marker marks the older copy as superseded.
        return _p.NoOrigin ? [WriteStep.BumpSetMarked, WriteStep.SrcDel] : [WriteStep.SrcDel, WriteStep.BumpSet];
    }

    private async ValueTask<WriteResult> WriteManagedAsync(string key, T? value, DateTimeOffset? deadline, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        var lease = new WritePermitLease(_node.Permits, _options.SkewBound, _options.FallbackPermitHold);
        try
        {
            return await WriteRunsAsync(key, value, deadline, plan, lease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease.Settle();
        }
    }

    private async ValueTask<WriteResult> WriteRunsAsync(string key, T? value, DateTimeOffset? deadline, KeyMovePlan plan, WritePermitLease lease, CancellationToken cancellationToken)
    {
        _retries.Supersede(key);
        var (delete, px) = WriteLifetime(value, deadline);
        var absolute = delete ? null : deadline;
        var writeValue = delete ? null : value;

        var history = new WriteHistory();
        var run = 0;
        while (true)
        {
            plan = await SettleWaitAsync(plan, cancellationToken).ConfigureAwait(false);
            if (plan.Mode == KeyMoveMode.Plain)
            {
                return await WritePlain(key, value, deadline, cancellationToken).ConfigureAwait(false);
            }

            if (await WriteAttemptAsync(key, writeValue, delete, px, absolute, plan, history, lease, run++, cancellationToken).ConfigureAwait(false) is { } result)
            {
                return result;
            }

            plan = PlanFor(write: true);
        }
    }

    // Null when the operation is built again under the state in force.
    private async ValueTask<WriteResult?> WriteAttemptAsync(string key, T? writeValue, bool delete, long px, DateTimeOffset? absolute, KeyMovePlan plan, WriteHistory history, WritePermitLease lease, int run, CancellationToken cancellationToken)
    {
        if (StepsFor(plan, delete) is not { } steps)
        {
            return ClosedOrUnknown(history.Applied);
        }

        if (!TryEncodeWrite(writeValue, steps, out var inPlace, out var native))
        {
            return WriteOutcome.NotSent;
        }

        if (await PrepareMarkedAsync(key, steps, history.Applied, lease, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var (admitted, replan) = await AdmitAsync(promotion: false, plan.Act?.Id, cancellationToken).ConfigureAwait(false);
        if (replan)
        {
            // The steps above were built under an older state: the whole operation is built again under the one in force.
            if (AdmissionRerunRefusal(run, history.Applied) is { } exhausted)
            {
                return exhausted;
            }

            await Task.Delay(_options.AdmissionPoll, _p.Clock, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (admitted is null)
        {
            return ClosedOrUnknown(history.Applied);
        }

        using var unit = admitted;
        var form = new Form(key, steps, inPlace, native, px, unit.Id, MoveScripts.NewNonce(), absolute) { First = !history.Attempted, Prior = history.Applied, Lease = lease };
        var ran = await RunStepsAsync(form, 0, unit, cancellationToken).ConfigureAwait(false);
        if (ran.Rerun)
        {
            history.Record(form.MarkerValue is not null);
        }

        return WriteFinished(ran, run, history.Applied);
    }

    private ValueTask<WriteResult> WritePlain(string key, T? value, DateTimeOffset? deadline, CancellationToken cancellationToken) =>
        value is null ? _p.Plain.RemoveAsync(key, cancellationToken) : _p.Plain.SetAsync(key, value, deadline, cancellationToken);

    private WriteResult? WriteFinished((WriteResult Result, bool Rerun) ran, int run, bool applied)
    {
        if (!ran.Rerun)
        {
            return ran.Result;
        }

        Count(IdentityRerun);
        return run >= _options.MaxReruns ? Unsent(applied) : null;
    }

    // After an applied attempt the write is uncertain, never refused: the call settles as Unknown.
    private WriteResult ClosedOrUnknown(bool applied) => applied ? Typed(WriteOutcome.Unknown) : throw Unavailable();

    private WriteResult FirstStepFailed(Form form, WriteOutcome failed) => Typed(form.Prior ? WriteOutcome.Unknown : failed);

    private WriteResult Unsent(bool applied) => applied ? Typed(WriteOutcome.Unknown) : WriteOutcome.NotSent;

    // A key whose field would be too long fails closed before any step; the permit is taken before the unit is admitted, so a wait for one never holds up a transition.
    private async ValueTask<WriteResult?> PrepareMarkedAsync(string key, WriteStep[] steps, bool applied, WritePermitLease lease, CancellationToken cancellationToken)
    {
        if (steps[0] != WriteStep.BumpSetMarked)
        {
            return null;
        }

        if (Encoding.UTF8.GetByteCount(_p.InPlaceKey(key)) > _options.MarkerKeyMax)
        {
            RouterTelemetry.CountMarker("marker_key_too_long", _p.CacheId, _p.Group);
            return Unsent(applied);
        }

        await lease.EnsureAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    private (bool Delete, long Px) WriteLifetime(T? value, DateTimeOffset? deadline)
    {
        if (value is null || deadline is null)
        {
            return (value is null, MoveScripts.NoExpiry);
        }

        var px = MillisecondsLeft(deadline.Value);
        return px < 1 ? (true, MoveScripts.NoExpiry) : (false, px);
    }

    private bool TryEncodeWrite(T? value, WriteStep[] steps, out byte[]? inPlace, out byte[]? native)
    {
        inPlace = null;
        native = null;
        try
        {
            if (value is not null)
            {
                inPlace = steps.Contains(WriteStep.SrcSet) ? Source.Codec.Encode(value) : null;
                native = steps.Contains(WriteStep.BumpSet) || steps.Contains(WriteStep.BumpSetMarked) || steps.Contains(WriteStep.NativeSet) ? _p.Native.Encode(value) : null;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private long MillisecondsLeft(DateTimeOffset deadline) => (long)Math.Ceiling((deadline - _p.Clock.GetUtcNow()).TotalMilliseconds);

    private void CheckAdmissionRerun(int run)
    {
        if (run >= _options.MaxReruns)
        {
            throw Unavailable();
        }
    }

    private WriteResult? AdmissionRerunRefusal(int run, bool applied) => run < _options.MaxReruns ? null : ClosedOrUnknown(applied);

    private async ValueTask<(WriteResult Result, bool Rerun)> RunStepsAsync(Form form, int from, AdmittedUnit unit, CancellationToken cancellationToken)
    {
        for (var i = from; i < form.Steps.Length; i++)
        {
            var sent = await SendAsync(form, form.Steps[i], cancellationToken).ConfigureAwait(false);
            if (sent.Marker is not null)
            {
                form.MarkerValue = sent.Marker;
            }

            if (sent.Failed is { } failed)
            {
                return (i == 0 ? FirstStepFailed(form, failed) : Pending(form, i), false);
            }

            if (MarkedSetRefused(form.Steps[i], sent))
            {
                return (Unsent(form.Prior), false);
            }

            if (sent.Code == MoveScripts.IdentityMismatch)
            {
                return IdentityChanged(form, i, unit);
            }
        }

        await FinishMarkAsync(form).ConfigureAwait(false);
        return (WriteOutcome.Committed, false);
    }

    // The identity changed: the whole unit runs again under the new rules, never retried under the old one.
    private (WriteResult Result, bool Rerun) IdentityChanged(Form form, int step, AdmittedUnit unit)
    {
        if (unit.Readmit())
        {
            return (default, true);
        }

        return (step == 0 ? Unsent(form.Prior) : Pending(form, step), false);
    }

    // The script changed nothing: a first attempt at the limit, a late command, or a record with no binding value. An earlier attempt of this write may have applied.
    private bool MarkedSetRefused(WriteStep step, StepSent sent)
    {
        if (step != WriteStep.BumpSetMarked || sent.Code is not (MoveScripts.RecordFull or MoveScripts.Late or MoveScripts.NoBinding))
        {
            return false;
        }

        var refusal = sent.Code switch
        {
            MoveScripts.RecordFull => "marker_capacity",
            MoveScripts.Late => "marker_late",
            _ => "marker_no_binding",
        };
        RouterTelemetry.CountMarker(refusal, _p.CacheId, _p.Group);
        return true;
    }

    // After the delete of L: the marker is removed later, in a batch, or before the caller is acknowledged while the node's queue is saturated.
    private async ValueTask FinishMarkAsync(Form form)
    {
        if (form.Steps[0] != WriteStep.BumpSetMarked || form.MarkerValue is not { } value || Data is not { } data)
        {
            return;
        }

        var control = ControlOf(data, Native(form.CallerKey));
        var field = data.Names.MarkerField(_p.InPlaceKey(form.CallerKey));
        if (Unmarks.Saturated)
        {
            try
            {
                _ = await data.Scripts.UnmarkAsync(control, field, value).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
            {
                // The queue gets it; a marker that stays is an orphan.
                _ = ex;
            }
        }

        Unmarks.Enqueue(control, field, value, _p.CacheId, _p.Group);
    }

    private WriteResult Typed(WriteOutcome failed) => failed == WriteOutcome.Unknown ? WriteResult.UnknownWithin(_p.SettleWithin()) : failed;

    // A later step that failed: retried with backoff until it succeeds, its write's life has passed, or the node stops. The call reports Unknown while it is pending.
    // A retry runs outside the epoch gate, so the drain of a transition does not wait for it; it is safe because every script checks the identity and the fence.
    private WriteResult Pending(Form form, int step)
    {
        _retries.Add(
            form.CallerKey,
            form.Deadline ?? _p.Clock.GetUtcNow() + _options.RetryLife,
            async token =>
            {
                for (var i = step; i < form.Steps.Length; i++)
                {
                    var sent = await SendAsync(form, form.Steps[i], token, retry: true).ConfigureAwait(false);
                    if (sent.Failed is not null)
                    {
                        return false;
                    }

                    // A newer write or a promotion moved the fence, or the identity changed: losing an unknown write is allowed, overwriting a newer value is not.
                    // A retry is never run again as an ordinary write, which would carry no fence condition.
                    if (sent.Code is MoveScripts.Superseded or MoveScripts.IdentityMismatch)
                    {
                        Count(RetrySuperseded);
                        return true;
                    }
                }

                await FinishMarkAsync(form).ConfigureAwait(false);
                return true;
            });
        return WriteResult.UnknownWithin(_p.SettleWithin());
    }

    private async ValueTask<StepSent> SendAsync(Form form, WriteStep step, CancellationToken cancellationToken, bool retry = false)
    {
        if (step is WriteStep.BumpSet or WriteStep.BumpDel && !retry)
        {
            await ObserveFenceAsync(form).ConfigureAwait(false);
        }

        await At(Point(step)).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested || Data is not { } data || !data.IsConnected() || SourceUnavailable(step is WriteStep.SrcDel or WriteStep.SrcSet))
        {
            return StepSent.NotSent;
        }

        var px = RetryLifetime(form, retry);
        if (ExpiredSet(step, px, retry && form.Deadline is not null))
        {
            return new StepSent(MoveScripts.Superseded);
        }

        var n = Native(form.CallerKey);
        if (step == WriteStep.BumpSetMarked)
        {
            return await SendMarkedAsync(data, form, n, px, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var code = step switch
            {
                WriteStep.SrcDel => await Source.DeleteAsync(form.CallerKey, form.Id).ConfigureAwait(false),
                WriteStep.SrcSet => await Source.SetAsync(form.CallerKey, form.Id, form.SourceBytes ?? throw MissingWriteValue(), px, false).ConfigureAwait(false),
                WriteStep.BumpSet when retry => await data.Scripts.BumpIfFenceAsync(Fenced(data, n), form.Id, form.Nonce, form.Observed, FenceTtlMs, BumpOperation.Set, form.NativeBytes ?? throw MissingWriteValue(), px).ConfigureAwait(false),
                WriteStep.BumpDel when retry => await data.Scripts.BumpIfFenceAsync(Fenced(data, n), form.Id, form.Nonce, form.Observed, FenceTtlMs, BumpOperation.Delete, markerField: MarkerFieldOf(data, form.CallerKey)).ConfigureAwait(false),
                WriteStep.BumpSet => await data.Scripts.BumpAsync(Fenced(data, n), form.Id, form.Nonce, FenceTtlMs, BumpOperation.Set, form.NativeBytes ?? throw MissingWriteValue(), px).ConfigureAwait(false),
                WriteStep.BumpDel => await data.Scripts.BumpAsync(Fenced(data, n), form.Id, form.Nonce, FenceTtlMs, BumpOperation.Delete, markerField: MarkerFieldOf(data, form.CallerKey)).ConfigureAwait(false),
                WriteStep.NativeSet => BooleanCode(await data.Db.StringSetAsync(Key(n), form.NativeBytes ?? throw MissingWriteValue(), Expiry(px), When.Always, Master).ConfigureAwait(false)),
                _ => BooleanCode(await data.Db.KeyDeleteAsync(Key(n), Master).ConfigureAwait(false)),
            };
            return new StepSent(code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return StepSent.Unknown;
        }
    }

    // A marked set with no reply may still apply; its permit is kept until it cannot.
    private async ValueTask<StepSent> SendMarkedAsync(KeyMoveData data, Form form, string n, long px, CancellationToken cancellationToken)
    {
        var now = _p.Clock.GetUtcNow();
        var deadline = now + _options.CommandTimeout;
        try
        {
            var reply = await data.Scripts.BumpMarkedAsync(Fenced(data, n), form.Id, form.Nonce, FenceTtlMs, form.NativeBytes ?? throw MissingWriteValue(), px, data.Names.MarkerField(_p.InPlaceKey(form.CallerKey)), form.First, deadline.ToUnixTimeMilliseconds()).ConfigureAwait(false);
            return new StepSent(reply.Committed ? 1 : reply.Code, null, reply.Marker);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            form.Lease?.Unanswered(deadline, now, data);
            return StepSent.Unknown;
        }
    }

    private string? MarkerFieldOf(KeyMoveData data, string key) => _p.NoOrigin ? data.Names.MarkerField(_p.InPlaceKey(key)) : null;

    // A retry keeps the original deadline.
    private long RetryLifetime(Form form, bool retry) =>
        retry && form.Deadline is { } deadline ? MillisecondsLeft(deadline) : form.Px;

    private FencedKeys Fenced(KeyMoveData data, string n) => new(data.Names.ForwardFence(n), Key(n), ControlOf(data, n));

    private CacheUnavailableException Unavailable() => new($"Cache '{_p.CacheId}' is closed for writes while its group moves.");

    // A write that waits for a new snapshot does so up to the time the rules left, then it is closed.
    private async ValueTask<KeyMovePlan> SettleWaitAsync(KeyMovePlan plan, CancellationToken cancellationToken)
    {
        if (plan.Mode != KeyMoveMode.Wait)
        {
            return plan;
        }

        var until = _p.Clock.GetTimestamp() + Ticks(plan.Rules.WriteWait ?? TimeSpan.Zero);
        while (plan.Mode == KeyMoveMode.Wait && _p.Clock.GetTimestamp() < until)
        {
            await Task.Delay(_options.AdmissionPoll, _p.Clock, cancellationToken).ConfigureAwait(false);
            plan = PlanFor(write: true);
        }

        return plan.Mode == KeyMoveMode.Wait ? plan with { Mode = KeyMoveMode.Closed } : plan;
    }

    // The first attempt of a fence step that has an earlier step may be retried, so the token in force just before it is kept: the retry may apply over it or over the nonce this attempt sets.
    private async Task ObserveFenceAsync(Form form)
    {
        if (form.Steps[0] is WriteStep.BumpSet or WriteStep.BumpSetMarked or WriteStep.BumpDel || form.Looked || Data is not { } data || !data.IsConnected())
        {
            return;
        }

        form.Looked = true;
        await At(SchedulePointStep.FenceObserve).ConfigureAwait(false);
        try
        {
            form.Observed = await data.Db.StringGetAsync(data.Names.ForwardFence(Native(form.CallerKey)), Master).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            // Unobserved: only the nonce of the first attempt can let the retry apply.
            form.Observed = null;
        }
    }

    private sealed record Form(string CallerKey, WriteStep[] Steps, byte[]? SourceBytes, byte[]? NativeBytes, long Px, string Id, string Nonce, DateTimeOffset? Deadline)
    {
        public string? Observed { get; set; }

        /// <summary>True only while no attempt of this logical write has been sent.</summary>
        public bool First { get; init; }

        /// <summary>An earlier attempt of this logical write applied its marked set.</summary>
        public bool Prior { get; init; }

        public WritePermitLease? Lease { get; init; }

        /// <summary>The marker this attempt's marked set wrote, as the script answered it.</summary>
        public string? MarkerValue { get; set; }

        public bool Looked { get; set; }
    }

    // The history of the logical write across its reruns: once an attempt has been sent, a rerun is never refused for the marker limit.
    private sealed class WriteHistory
    {
        public bool Attempted { get; private set; }

        public bool Applied { get; private set; }

        public void Record(bool applied)
        {
            Attempted = true;
            Applied |= applied;
        }
    }
}
