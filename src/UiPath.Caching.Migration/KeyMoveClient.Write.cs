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
        WriteStep.BumpSet or WriteStep.BumpDel => SchedulePointStep.Bump,
        _ => SchedulePointStep.NativeWrite,
    };

    private static long BooleanCode(bool result) => result ? 1 : 0;

    private static TimeSpan? Expiry(long px) => px < 0 ? null : TimeSpan.FromMilliseconds(px);

    private static InvalidOperationException MissingWriteValue() => new("A set step requires an encoded value.");

    private static bool ExpiredSet(WriteStep step, long px, bool boundedRetry) =>
        boundedRetry && px < 1 && step is WriteStep.SrcSet or WriteStep.BumpSet or WriteStep.NativeSet;

    private WriteStep[] DualWriteSteps(bool delete)
    {
        if (delete)
        {
            return [WriteStep.SrcDel, WriteStep.BumpDel];
        }

        // NoOrigin writes N first to avoid a miss between the steps.
        return _p.NoOrigin ? [WriteStep.BumpSet, WriteStep.SrcDel] : [WriteStep.SrcDel, WriteStep.BumpSet];
    }

    private async ValueTask<WriteResult> WriteManagedAsync(string key, T? value, DateTimeOffset? deadline, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        _retries.Supersede(key);
        var (delete, px) = WriteLifetime(value, deadline);
        var absolute = delete ? null : deadline;
        var writeValue = delete ? null : value;

        var run = 0;
        while (true)
        {
            plan = await SettleWaitAsync(plan, cancellationToken).ConfigureAwait(false);
            if (plan.Mode == KeyMoveMode.Plain)
            {
                return await WritePlain(key, value, deadline, cancellationToken).ConfigureAwait(false);
            }

            if (StepsFor(plan, delete) is not { } steps)
            {
                throw Unavailable();
            }

            if (!TryEncodeWrite(writeValue, steps, out var inPlace, out var native))
            {
                return WriteOutcome.NotSent;
            }

            var (admitted, replan) = await AdmitAsync(promotion: false, plan.Act?.Id, cancellationToken).ConfigureAwait(false);
            if (replan)
            {
                // The steps above were built under an older state: the whole operation is built again under the one in force.
                CheckAdmissionRerun(run++);
                await Task.Delay(_options.AdmissionPoll, _p.Clock, cancellationToken).ConfigureAwait(false);
                plan = PlanFor(write: true);
                continue;
            }

            using var unit = admitted ?? throw Unavailable();
            var form = new Form(key, steps, inPlace, native, px, unit.Id, MoveScripts.NewNonce(), absolute);
            var ran = await RunStepsAsync(form, 0, unit, cancellationToken).ConfigureAwait(false);
            if (WriteFinished(ran, run++) is { } result)
            {
                return result;
            }

            plan = PlanFor(write: true);
        }
    }

    private ValueTask<WriteResult> WritePlain(string key, T? value, DateTimeOffset? deadline, CancellationToken cancellationToken) =>
        value is null ? _p.Plain.RemoveAsync(key, cancellationToken) : _p.Plain.SetAsync(key, value, deadline, cancellationToken);

    private WriteResult? WriteFinished((WriteResult Result, bool Rerun) ran, int run)
    {
        if (!ran.Rerun)
        {
            return ran.Result;
        }

        Count(IdentityRerun);
        return run >= _options.MaxReruns ? WriteOutcome.NotSent : null;
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
                inPlace = steps.Contains(WriteStep.SrcSet) ? _p.InPlace.Encode(value) : null;
                native = steps.Contains(WriteStep.BumpSet) || steps.Contains(WriteStep.NativeSet) ? _p.Native.Encode(value) : null;
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

    private async ValueTask<(WriteResult Result, bool Rerun)> RunStepsAsync(Form form, int from, AdmittedUnit unit, CancellationToken cancellationToken)
    {
        for (var i = from; i < form.Steps.Length; i++)
        {
            var sent = await SendAsync(form, form.Steps[i], cancellationToken).ConfigureAwait(false);
            if (sent.Failed is { } failed)
            {
                return (i == 0 ? Typed(failed) : Pending(form, i), false);
            }

            if (sent.Code == MoveScripts.IdentityMismatch)
            {
                // The identity changed: the whole unit runs again under the new rules, never retried under the old one.
                if (unit.Readmit())
                {
                    return (default, true);
                }

                return (i == 0 ? WriteOutcome.NotSent : Pending(form, i), false);
            }
        }

        return (WriteOutcome.Committed, false);
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
        if (cancellationToken.IsCancellationRequested || Data is not { } data || !data.IsConnected())
        {
            return StepSent.NotSent;
        }

        var px = RetryLifetime(form, retry);
        if (ExpiredSet(step, px, retry && form.Deadline is not null))
        {
            return new StepSent(MoveScripts.Superseded);
        }

        var l = InPlace(form.CallerKey);
        var n = Native(form.CallerKey);
        try
        {
            var code = step switch
            {
                WriteStep.SrcDel => await data.Scripts.SourceDeleteAsync(Key(l), ControlOf(data, l), form.Id).ConfigureAwait(false),
                WriteStep.SrcSet => await data.Scripts.SourceSetAsync(Key(l), ControlOf(data, l), form.Id, form.SourceBytes ?? throw MissingWriteValue(), px, false).ConfigureAwait(false),
                WriteStep.BumpSet when retry => await data.Scripts.BumpIfFenceAsync(Fenced(data, n), form.Id, form.Nonce, form.Observed, FenceTtlMs, BumpOperation.Set, form.NativeBytes ?? throw MissingWriteValue(), px).ConfigureAwait(false),
                WriteStep.BumpDel when retry => await data.Scripts.BumpIfFenceAsync(Fenced(data, n), form.Id, form.Nonce, form.Observed, FenceTtlMs, BumpOperation.Delete).ConfigureAwait(false),
                WriteStep.BumpSet => await data.Scripts.BumpAsync(Fenced(data, n), form.Id, form.Nonce, FenceTtlMs, BumpOperation.Set, form.NativeBytes ?? throw MissingWriteValue(), px).ConfigureAwait(false),
                WriteStep.BumpDel => await data.Scripts.BumpAsync(Fenced(data, n), form.Id, form.Nonce, FenceTtlMs, BumpOperation.Delete).ConfigureAwait(false),
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
        if (form.Steps[0] is WriteStep.BumpSet or WriteStep.BumpDel || form.Looked || Data is not { } data || !data.IsConnected())
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

        public bool Looked { get; set; }
    }
}
