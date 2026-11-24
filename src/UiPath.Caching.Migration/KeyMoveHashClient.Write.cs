using System.Text;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed partial class KeyMoveHashClient<T>
{
    // A merge repeats the promotion this often while L still holds the hash, then fails closed as changing.
    private const int MaxEnsureAttempts = 3;

    private enum TurnKind
    {
        Proceed,
        Again,
        Done,
    }

    public ValueTask<WriteResult> SetAsync(string key, IDictionary<string, T?> fields, HashWriteOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var plan = PlanFor(write: true);
        if (plan.Mode == KeyMoveMode.Plain)
        {
            return _p.Plain.SetAsync(key, fields, options, cancellationToken);
        }

        ThrowIfMetadataRefused(options.Metadata, plan.Mode);

        return WriteManagedAsync(key, options.Mode == HashCacheSetOption.KeyReplace ? HashWriteKind.Replace : HashWriteKind.Merge, fields, options, null, plan, cancellationToken);
    }

    public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        var plan = PlanFor(write: true);
        return plan.Mode == KeyMoveMode.Plain ? _p.Plain.RemoveAsync(key, cancellationToken) : WriteManagedAsync(key, HashWriteKind.Delete, null, default, null, plan, cancellationToken);
    }

    public ValueTask<WriteResult> RemoveFieldsAsync(string key, string[] fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var plan = PlanFor(write: true);
        return plan.Mode == KeyMoveMode.Plain ? _p.Plain.RemoveFieldsAsync(key, fields, cancellationToken) : WriteManagedAsync(key, HashWriteKind.DeleteFields, null, default, fields, plan, cancellationToken);
    }

    // Where L is written, a format that has no field for metadata could not keep it: the write is refused before anything is stored.
    internal void ThrowIfMetadataRefused(IDictionary<string, string?>? metadata, KeyMoveMode mode)
    {
        if (metadata is not null && mode is KeyMoveMode.S1 or KeyMoveMode.S2 && !_p.InPlace.CarriesMetadata)
        {
            throw new NotSupportedException($"The hash cache '{_p.CacheId}' keeps its hashes in a format that has no field for metadata until its keys move, so a write that carries metadata cannot run in state {mode}.");
        }
    }

    /// <summary>The steps of a write form for a state, in the order they run; null where the cache is closed.</summary>
    /// <param name="residue">For a field delete in S4: L holds the hash, so its field goes first.</param>
    internal HashWriteStep[]? StepsFor(KeyMovePlan plan, HashWriteKind kind, bool residue = false)
    {
        var src = kind switch
        {
            HashWriteKind.Delete => HashWriteStep.SrcDel,
            HashWriteKind.DeleteFields => HashWriteStep.SrcFieldsDel,
            _ => HashWriteStep.SrcWrite,
        };
        switch (plan.Mode)
        {
            case KeyMoveMode.S1:
                return [src];
            case KeyMoveMode.S2:
                return _p.NoOrigin ? null : [src];
            case KeyMoveMode.S3:
            case KeyMoveMode.S4:
                return MovingSteps(plan, kind, residue);
            case KeyMoveMode.S5:
                return MovedSteps(plan, kind);
            default:
                return null;
        }
    }

    private static SchedulePointStep Point(HashWriteStep step) => step switch
    {
        HashWriteStep.SrcWrite => SchedulePointStep.SourceSet,
        HashWriteStep.SrcDel or HashWriteStep.SrcFieldsDel => SchedulePointStep.SourceDelete,
        HashWriteStep.DstWrite or HashWriteStep.DstWriteMarked => SchedulePointStep.HashWrite,
        HashWriteStep.BumpDel => SchedulePointStep.Bump,
        HashWriteStep.DstFieldsDel => SchedulePointStep.HashDelete,
        _ => SchedulePointStep.NativeWrite,
    };

    // A first step that failed changed nothing, unless an earlier attempt of this write applied its marked set.
    private static WriteOutcome FirstStepFailure(Form form, WriteOutcome failed) => form.Prior ? WriteOutcome.Unknown : failed;

    private static HashEntry[] EntriesOf(IReadOnlyList<KeyValuePair<string, byte[]>> stored) =>
        [.. stored.Select(e => new HashEntry(e.Key, e.Value))];

    private static HashWriteStep[] MovedSteps(KeyMovePlan plan, HashWriteKind kind)
    {
        var last = kind switch
        {
            HashWriteKind.Delete => HashWriteStep.NativeDel,
            HashWriteKind.DeleteFields => HashWriteStep.NativeFieldsDel,
            _ => HashWriteStep.NativeWrite,
        };

        // A merge that left its L copy would be served by a union reader after N expires, so it deletes the whole key of L like a replace.
        var first = kind == HashWriteKind.DeleteFields ? HashWriteStep.SrcFieldsDel : HashWriteStep.SrcDel;
        return plan.Act is { Swept: true } ? [last] : [first, last];
    }

    private static bool NeedsSourceProbe(WriteRun run) =>
        !run.Applied && (run.Kind == HashWriteKind.Merge && run.Plan.Mode is KeyMoveMode.S3 or KeyMoveMode.S4 || run.Kind == HashWriteKind.DeleteFields && run.Plan.Mode == KeyMoveMode.S4);

    private static Turn EmptyWrite(WriteRun run)
    {
        if (run.Kind == HashWriteKind.Merge)
        {
            return Turn.Done(WriteOutcome.Committed);
        }

        run.Kind = HashWriteKind.Delete;
        return Turn.Again;
    }

    private HashWriteStep[] MovingSteps(KeyMovePlan plan, HashWriteKind kind, bool residue) => kind switch
    {
        HashWriteKind.Delete => [HashWriteStep.SrcDel, HashWriteStep.BumpDel],

        // Forward NoOrigin sets Dst first, so a stored hash never reads as a miss between the steps. The marker marks the older copy as superseded.
        HashWriteKind.Replace when _p.NoOrigin => [HashWriteStep.DstWriteMarked, HashWriteStep.SrcDel],
        HashWriteKind.Replace => [HashWriteStep.SrcDel, HashWriteStep.DstWrite],

        // A merge runs after the hash has been promoted, or when L holds none.
        HashWriteKind.Merge => [HashWriteStep.DstWrite],
        _ => plan.Mode == KeyMoveMode.S4 && !residue ? [HashWriteStep.DstFieldsDel] : [HashWriteStep.SrcFieldsDel, HashWriteStep.DstFieldsDel],
    };

    private async ValueTask<WriteResult> WriteManagedAsync(string key, HashWriteKind kind, IDictionary<string, T?>? fields, HashWriteOptions options, string[]? names, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        var lease = new WritePermitLease(_node.Permits, _options.SkewBound, _options.FallbackPermitHold);
        try
        {
            return await WriteRunsAsync(key, kind, fields, options, names, plan, lease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease.Settle();
        }
    }

    private async ValueTask<WriteResult> WriteRunsAsync(string key, HashWriteKind kind, IDictionary<string, T?>? fields, HashWriteOptions options, string[]? names, KeyMovePlan plan, WritePermitLease lease, CancellationToken cancellationToken)
    {
        _retries.Supersede(key);
        var run = StartRun(new WriteRun(key, fields, options, names, lease) { Kind = kind, Plan = plan });
        while (true)
        {
            run.Number++;
            var turn = await WriteTurnAsync(run, cancellationToken).ConfigureAwait(false);
            if (turn.Kind == TurnKind.Done)
            {
                return turn.Result;
            }
        }
    }

    // A write with a deadline carries it as an absolute expiry; one whose life has passed is a delete.
    private WriteRun StartRun(WriteRun run)
    {
        if (run.Kind is HashWriteKind.Replace or HashWriteKind.Merge && run.Options.Deadline is { } at)
        {
            // A remaining life that is a count below 1 ms is a delete; none is not.
            if ((long)Math.Ceiling((at - _p.Clock.GetUtcNow()).TotalMilliseconds) < 1)
            {
                run.Kind = HashWriteKind.Delete;
            }
            else
            {
                run.ExpireAt = at.ToUnixTimeMilliseconds();
                run.Absolute = at;
            }
        }

        return run;
    }

    // One pass of a logical write: the state is settled, the source probed, the steps built, admitted and sent.
    private async ValueTask<Turn> WriteTurnAsync(WriteRun run, CancellationToken cancellationToken)
    {
        var turn = await SettleAndProbeAsync(run, cancellationToken).ConfigureAwait(false);
        if (turn.Kind != TurnKind.Proceed)
        {
            return turn;
        }

        turn = await PrepareStepsAsync(run, cancellationToken).ConfigureAwait(false);
        if (turn.Kind != TurnKind.Proceed)
        {
            return turn;
        }

        return await SendFormAsync(run, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Turn> SettleAndProbeAsync(WriteRun run, CancellationToken cancellationToken)
    {
        run.Plan = await SettleWaitAsync(run.Plan, cancellationToken).ConfigureAwait(false);
        if (run.Plan.Mode == KeyMoveMode.Plain)
        {
            return Turn.Done(await PlainAsync(run.KeyName, run.Kind, run.Fields, run.Options, run.Names, cancellationToken).ConfigureAwait(false));
        }

        // The state can have changed under a wait or a new plan: the capability of L is judged again before anything is encoded or sent.
        ThrowIfMetadataRefused(run.Options.Metadata, run.Plan.Mode);
        return await ProbeBeforeWriteAsync(run, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Turn> ProbeBeforeWriteAsync(WriteRun run, CancellationToken cancellationToken)
    {
        run.Residue = false;
        if (!NeedsSourceProbe(run))
        {
            return Turn.Proceed;
        }

        var (probeRefusal, replanned, probedResidue) = await ProbeSourceAsync(run.KeyName, run.Kind, run.Plan, cancellationToken).ConfigureAwait(false);
        if (probeRefusal is { } refused)
        {
            return Turn.Done(refused);
        }

        if (replanned)
        {
            Count("identity_rerun");
            if (run.Number >= _options.MaxReruns)
            {
                throw Unavailable();
            }

            run.Plan = PlanFor(write: true);
            return Turn.Again;
        }

        run.Residue = probedResidue;
        return Turn.Proceed;
    }

    // The steps of the form, the fields they carry and the marker key they need.
    private async ValueTask<Turn> PrepareStepsAsync(WriteRun run, CancellationToken cancellationToken)
    {
        if (StepsFor(run.Plan, run.Kind, run.Residue) is not { } steps)
        {
            // After an applied attempt the write is uncertain, never refused: the call settles as Unknown.
            return Turn.Done(run.Applied ? Typed(WriteOutcome.Unknown) : throw Unavailable());
        }

        run.Steps = steps;
        if (!TryEncodeWrite(run))
        {
            return Turn.Done(WriteOutcome.NotSent);
        }

        // A write that stores no key is a delete of the key, and a merge of nothing changes nothing.
        if ((run.InPlaceEntries?.Length == 0 || run.NativeEntries?.Length == 0) && run.Kind != HashWriteKind.Delete)
        {
            return EmptyWrite(run);
        }

        return await ReserveMarkerAsync(run, cancellationToken).ConfigureAwait(false);
    }

    private bool TryEncodeWrite(WriteRun run)
    {
        run.InPlaceEntries = null;
        run.NativeEntries = null;
        try
        {
            if (run.Kind is HashWriteKind.Replace or HashWriteKind.Merge)
            {
                var inPlaceMetadata = _p.InPlace.CarriesMetadata ? run.Options.Metadata : null;
                run.InPlaceEntries = run.Steps.Contains(HashWriteStep.SrcWrite) ? EntriesOf(_p.InPlace.EncodeFields(run.Fields!, inPlaceMetadata)) : null;
                run.NativeEntries = run.Steps.Contains(HashWriteStep.DstWrite) || run.Steps.Contains(HashWriteStep.DstWriteMarked) || run.Steps.Contains(HashWriteStep.NativeWrite) ? EntriesOf(_p.Native.EncodeFields(run.Fields!, run.Options.Metadata)) : null;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async ValueTask<Turn> ReserveMarkerAsync(WriteRun run, CancellationToken cancellationToken)
    {
        if (run.Steps[0] != HashWriteStep.DstWriteMarked)
        {
            return Turn.Proceed;
        }

        // A key whose field would be too long fails closed before any step; the permit is taken before the unit is admitted, so a wait for one never holds up a transition.
        if (Encoding.UTF8.GetByteCount(InPlace(run.KeyName)) > _options.MarkerKeyMax)
        {
            RouterTelemetry.CountMarker("marker_key_too_long", _p.CacheId, _p.Group);
            return Turn.Done(run.Applied ? Typed(WriteOutcome.Unknown) : WriteOutcome.NotSent);
        }

        await run.Lease.EnsureAsync(cancellationToken).ConfigureAwait(false);
        return Turn.Proceed;
    }

    private async ValueTask<Turn> SendFormAsync(WriteRun run, CancellationToken cancellationToken)
    {
        var (admitted, replan) = Admit(promotion: false, run.Plan.Act?.Id);
        if (replan)
        {
            return await ReplanWriteAsync(run, cancellationToken).ConfigureAwait(false);
        }

        if (admitted is null && run.Applied)
        {
            return Turn.Done(Typed(WriteOutcome.Unknown));
        }

        using var unit = admitted ?? throw Unavailable();
        var form = new Form(run.KeyName, run.Steps, run.InPlaceEntries, run.NativeEntries, run.Kind == HashWriteKind.Replace ? HashCacheSetOption.KeyReplace : HashCacheSetOption.HashReplace, run.ExpireAt, run.Names, unit.Id, MoveScripts.NewNonce(), run.Absolute, HashVersion.NewEpochSeed()) { First = !run.Attempted, Prior = run.Applied, Lease = run.Lease };
        var (ranResult, rerun) = await RunStepsAsync(form, unit, cancellationToken).ConfigureAwait(false);
        if (!rerun)
        {
            return Turn.Done(ranResult);
        }

        run.Attempted = true;
        run.Applied |= form.MarkerValue is not null;
        Count("identity_rerun");
        if (run.Number >= _options.MaxReruns)
        {
            return Turn.Done(run.Applied ? Typed(WriteOutcome.Unknown) : WriteOutcome.NotSent);
        }

        run.Plan = PlanFor(write: true);
        return Turn.Again;
    }

    // The steps above were built under an older state: the whole operation is built again under the one in force.
    private async ValueTask<Turn> ReplanWriteAsync(WriteRun run, CancellationToken cancellationToken)
    {
        if (run.Number >= _options.MaxReruns)
        {
            return Turn.Done(run.Applied ? Typed(WriteOutcome.Unknown) : throw Unavailable());
        }

        await Task.Delay(_options.AdmissionPoll, _p.Clock, cancellationToken).ConfigureAwait(false);
        run.Plan = PlanFor(write: true);
        return Turn.Again;
    }

    private ValueTask<WriteResult> PlainAsync(string key, HashWriteKind kind, IDictionary<string, T?>? fields, HashWriteOptions options, string[]? names, CancellationToken cancellationToken) => kind switch
    {
        HashWriteKind.Delete => _p.Plain.RemoveAsync(key, cancellationToken),
        HashWriteKind.DeleteFields => _p.Plain.RemoveFieldsAsync(key, names!, cancellationToken),
        _ => _p.Plain.SetAsync(key, fields!, options, cancellationToken),
    };

    // For a merge in S3 and S4, and the residue question of a field delete in S4. A merge on an N that is partial would hide every field of L that it did not name, so it never runs ahead of the promotion.
    private async ValueTask<(WriteResult? Refused, bool Replan, bool Residue)> ProbeSourceAsync(string key, HashWriteKind kind, KeyMovePlan plan, CancellationToken cancellationToken)
    {
        var present = await _reads.ExistsAsync(InPlace(key), SchedulePointStep.ProbeSource, cancellationToken).ConfigureAwait(false);
        if (present is null)
        {
            return (WriteOutcome.NotSent, false, false);
        }

        if (kind == HashWriteKind.DeleteFields || present == false)
        {
            return (null, false, present == true);
        }

        if (plan.Mode == KeyMoveMode.S4)
        {
            // S4 has no promoter: a hash that L still holds, promotable or not, stays whole, and a partial N would hide it.
            return (Blocked("src_residue"), false, true);
        }

        for (var attempt = 0; attempt < MaxEnsureAttempts; attempt++)
        {
            var promoted = await _promotion.PromoteAsync(key, HashPromotionFlavour.Ensure, cancellationToken).ConfigureAwait(false);
            switch (promoted.Status)
            {
                case HashPromotionStatus.Complete:
                    return (null, false, false);
                case HashPromotionStatus.TooLarge:
                    return (Blocked("too_large"), false, true);
                case HashPromotionStatus.Unreadable:
                    return (Blocked("unreadable"), false, true);
                case HashPromotionStatus.Replan:
                    return (null, true, false);
                case HashPromotionStatus.Unavailable:
                    return (WriteOutcome.NotSent, false, false);
            }
        }

        return (Blocked("changing"), false, true);
    }

    private WriteResult Blocked(string reason)
    {
        RouterTelemetry.CountMove("merge_blocked", _p.CacheId, _p.Group, reason);
        return WriteOutcome.NotSent;
    }

    private async ValueTask<(WriteResult Result, bool Rerun)> RunStepsAsync(Form form, AdmittedUnit unit, CancellationToken cancellationToken)
    {
        for (var i = 0; i < form.Steps.Length; i++)
        {
            var sent = await SendAsync(form, form.Steps[i], cancellationToken).ConfigureAwait(false);
            if (sent.Marker is not null)
            {
                form.MarkerValue = sent.Marker;
            }

            if (JudgeStep(form, unit, i, sent) is { } stopped)
            {
                return stopped;
            }
        }

        await FinishMarkAsync(form).ConfigureAwait(false);
        return (WriteOutcome.Committed, false);
    }

    // The way a step ended, when it ends the write; null when the next step is to run.
    private (WriteResult Result, bool Rerun)? JudgeStep(Form form, AdmittedUnit unit, int index, StepSent sent)
    {
        if (sent.Failed is { } failed)
        {
            return index != 0 ? (Pending(form, index), false) : (Typed(FirstStepFailure(form, failed)), false);
        }

        if (form.Steps[index] == HashWriteStep.DstWriteMarked && sent.Code is MoveScripts.RecordFull or MoveScripts.Late or MoveScripts.NoBinding)
        {
            return (MarkedWriteRefused(form, sent.Code), false);
        }

        return sent.Code == MoveScripts.IdentityMismatch ? IdentityChanged(form, unit, index) : null;
    }

    // The script changed nothing: a first attempt at the limit, a late command, or a record with no binding value. An earlier attempt of this write may have applied.
    private WriteResult MarkedWriteRefused(Form form, long code)
    {
        var refusal = code switch
        {
            MoveScripts.RecordFull => "marker_capacity",
            MoveScripts.Late => "marker_late",
            _ => "marker_no_binding",
        };
        RouterTelemetry.CountMarker(refusal, _p.CacheId, _p.Group);
        return form.Prior ? Typed(WriteOutcome.Unknown) : WriteOutcome.NotSent;
    }

    // The identity changed: the whole unit runs again under the new rules, never retried under the old one.
    private (WriteResult Result, bool Rerun) IdentityChanged(Form form, AdmittedUnit unit, int index)
    {
        if (unit.Readmit())
        {
            return (default, true);
        }

        if (index != 0)
        {
            return (Pending(form, index), false);
        }

        return (form.Prior ? Typed(WriteOutcome.Unknown) : WriteOutcome.NotSent, false);
    }

    // After the delete of L: the marker is removed later, in a batch, or before the caller is acknowledged while the node's queue is saturated.
    private async ValueTask FinishMarkAsync(Form form)
    {
        if (form.Steps[0] != HashWriteStep.DstWriteMarked || form.MarkerValue is not { } value || Data is not { } data)
        {
            return;
        }

        var control = ControlOf(data, Native(form.KeyName));
        var field = data.Names.MarkerField(InPlace(form.KeyName));
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

    // A later step that failed: retried with backoff until it succeeds, its write's life has passed, or the node stops. The call reports Unknown while it is pending.
    // A retry runs outside the epoch gate, so the drain of a transition does not wait for it; it is safe because every script checks the identity and a Dst step also the fence.
    private WriteResult Pending(Form form, int step)
    {
        _retries.Add(
            form.KeyName,
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
                        Count("retry_superseded");
                        return true;
                    }
                }

                await FinishMarkAsync(form).ConfigureAwait(false);
                return true;
            });
        return WriteResult.UnknownWithin(_p.SettleWithin());
    }

    private async ValueTask<StepSent> SendAsync(Form form, HashWriteStep step, CancellationToken cancellationToken, bool retry = false)
    {
        if (step is HashWriteStep.DstWrite or HashWriteStep.DstFieldsDel or HashWriteStep.BumpDel && !retry)
        {
            await ObserveFenceAsync(form).ConfigureAwait(false);
        }

        await At(Point(step)).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested || Data is not { } data || !data.IsConnected())
        {
            return StepSent.NotSent;
        }

        // A retry of a write whose life has passed is never sent.
        if (retry && form.Deadline is { } deadline && (long)Math.Ceiling((deadline - _p.Clock.GetUtcNow()).TotalMilliseconds) < 1 && step is HashWriteStep.SrcWrite or HashWriteStep.DstWrite or HashWriteStep.NativeWrite or HashWriteStep.DstWriteMarked)
        {
            return new StepSent(MoveScripts.Superseded);
        }

        var n = Native(form.KeyName);
        if (step == HashWriteStep.DstWriteMarked)
        {
            return await SendMarkedAsync(data, form, n, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return new StepSent(await StepScriptAsync(data, form, step, retry).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return StepSent.Unknown;
        }
    }

    private async ValueTask<long> StepScriptAsync(KeyMoveData data, Form form, HashWriteStep step, bool retry)
    {
        var l = InPlace(form.KeyName);
        var n = Native(form.KeyName);
        var fence = new FenceBump(data.Names.ForwardFence(n), form.Nonce, FenceTtlMs);
        var conditional = retry ? new FenceRetry(form.Observed) : (FenceRetry?)null;
        return step switch
        {
            HashWriteStep.SrcDel => await data.Scripts.SourceDeleteAsync(Key(l), ControlOf(data, l), form.Id).ConfigureAwait(false),
            HashWriteStep.SrcWrite => await data.Scripts.HashWriteAsync(Key(l), ControlOf(data, l), form.Id, new ControlledHashWrite(form.InPlace!, form.Mode, form.ExpireAt)).ConfigureAwait(false),
            HashWriteStep.SrcFieldsDel => await data.Scripts.HashDeleteFieldsAsync(Key(l), ControlOf(data, l), form.Id, form.Fields()).ConfigureAwait(false),
            HashWriteStep.DstWrite => await data.Scripts.HashWriteAsync(Key(n), ControlOf(data, n), form.Id, form.Counting(form.Native!), fence, conditional).ConfigureAwait(false),
            HashWriteStep.DstFieldsDel => await data.Scripts.HashDeleteFieldsAsync(Key(n), ControlOf(data, n), form.Id, form.Fields(), KnownFieldNames.VersionKey, form.Seed, fence, conditional).ConfigureAwait(false),
            HashWriteStep.BumpDel when retry => await data.Scripts.BumpIfFenceAsync(Fenced(data, n), form.Id, form.Nonce, form.Observed, FenceTtlMs, BumpOperation.Delete, markerField: _p.NoOrigin ? data.Names.MarkerField(l) : null).ConfigureAwait(false),
            HashWriteStep.BumpDel => await data.Scripts.BumpAsync(Fenced(data, n), form.Id, form.Nonce, FenceTtlMs, BumpOperation.Delete, markerField: _p.NoOrigin ? data.Names.MarkerField(l) : null).ConfigureAwait(false),
            HashWriteStep.NativeWrite => await data.Scripts.HashWriteAsync(Key(n), ControlOf(data, n), form.Id, form.Counting(form.Native!)).ConfigureAwait(false),
            HashWriteStep.NativeFieldsDel => await data.Scripts.HashDeleteFieldsAsync(Key(n), ControlOf(data, n), form.Id, form.Fields(), KnownFieldNames.VersionKey, form.Seed).ConfigureAwait(false),
            _ => await data.Db.KeyDeleteAsync(Key(n), Master).ConfigureAwait(false) ? 1L : 0L,
        };
    }

    // A marked write with no reply may still apply; its permit is kept until it cannot.
    private async ValueTask<StepSent> SendMarkedAsync(KeyMoveData data, Form form, string n, CancellationToken cancellationToken)
    {
        var now = _p.Clock.GetUtcNow();
        var deadline = now + _options.CommandTimeout;
        try
        {
            var reply = await data.Scripts.HashWriteMarkedAsync(Key(n), ControlOf(data, n), form.Id, form.Counting(form.Native!), new FenceBump(data.Names.ForwardFence(n), form.Nonce, FenceTtlMs), data.Names.MarkerField(InPlace(form.KeyName)), form.First, deadline.ToUnixTimeMilliseconds()).ConfigureAwait(false);
            return new StepSent(reply.Committed ? 1 : reply.Code, null, reply.Marker);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            form.Lease?.Unanswered(deadline, now, data);
            return StepSent.Unknown;
        }
    }

    // The first attempt of a fence step that has an earlier step may be retried, so the token in force just before it is kept: the retry may apply over it or over the nonce this attempt sets.
    private async Task ObserveFenceAsync(Form form)
    {
        if (form.Steps[0] is HashWriteStep.DstWrite or HashWriteStep.DstWriteMarked or HashWriteStep.DstFieldsDel or HashWriteStep.BumpDel || form.Looked || Data is not { } data || !data.IsConnected())
        {
            return;
        }

        form.Looked = true;
        await At(SchedulePointStep.FenceObserve).ConfigureAwait(false);
        try
        {
            form.Observed = await data.Db.StringGetAsync(data.Names.ForwardFence(Native(form.KeyName)), Master).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            // Unobserved: only the nonce of the first attempt can let the retry apply.
            form.Observed = null;
        }
    }

    private readonly record struct Turn(TurnKind Kind, WriteResult Result = default)
    {
        public static Turn Proceed => new(TurnKind.Proceed);

        public static Turn Again => new(TurnKind.Again);

        public static Turn Done(WriteResult result) => new(TurnKind.Done, result);
    }

    // The state of one logical write across its reruns: once an attempt has been sent, a rerun is never refused for the marker limit.
    private sealed class WriteRun(string key, IDictionary<string, T?>? fields, HashWriteOptions options, string[]? names, WritePermitLease lease)
    {
        public string KeyName => key;

        public IDictionary<string, T?>? Fields => fields;

        public HashWriteOptions Options => options;

        public string[]? Names => names;

        public WritePermitLease Lease => lease;

        public required HashWriteKind Kind { get; set; }

        public required KeyMovePlan Plan { get; set; }

        public long ExpireAt { get; set; } = MoveScripts.NoExpiry;

        public DateTimeOffset? Absolute { get; set; }

        public bool Attempted { get; set; }

        public bool Applied { get; set; }

        public int Number { get; set; } = -1;

        public bool Residue { get; set; }

        public HashWriteStep[] Steps { get; set; } = [];

        public HashEntry[]? InPlaceEntries { get; set; }

        public HashEntry[]? NativeEntries { get; set; }
    }

    private sealed record Form(string KeyName, HashWriteStep[] Steps, HashEntry[]? InPlace, HashEntry[]? Native, HashCacheSetOption Mode, long ExpireAt, string[]? Names, string Id, string Nonce, DateTimeOffset? Deadline, long Seed)
    {
        public string? Observed { get; set; }

        /// <summary>True only while no attempt of this logical write has been sent.</summary>
        public bool First { get; init; }

        /// <summary>An earlier attempt of this logical write applied its marked set.</summary>
        public bool Prior { get; init; }

        public WritePermitLease? Lease { get; init; }

        /// <summary>The marker this attempt's marked write wrote, as the script answered it.</summary>
        public string? MarkerValue { get; set; }

        public bool Looked { get; set; }

        /// <summary>The fields of a field delete, as the scripts take them.</summary>
        public IReadOnlyList<RedisValue> Fields() => [.. (Names ?? []).Select(f => (RedisValue)f)];

        /// <summary>The write of the native side: it counts its writes in the field the library keeps.</summary>
        public ControlledHashWrite Counting(HashEntry[] entries) => new(entries, Mode, ExpireAt, null, KnownFieldNames.VersionKey, Seed);
    }
}
