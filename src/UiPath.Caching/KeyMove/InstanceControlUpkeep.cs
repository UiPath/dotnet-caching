using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed class InstanceControlUpkeep(IRecordReader records, ITransitionSequence sequence, StateStore store, InstanceClaimRecovery claims, KeyMoveStateOptions options, TimeProvider time, Action<string, string>? report = null)
{
    public async Task<(UpkeepVerdict Verdict, bool SrcMissing)> CheckAsync(string group, StateRecord state, CancellationToken cancellationToken)
    {
        if (!ControlUpkeep.Applies(state))
        {
            return (UpkeepVerdict.NotApplicable, false);
        }

        var verdict = UpkeepVerdict.Healthy;
        var all = await records.ReadAllAsync(group, cancellationToken).ConfigureAwait(false);
        var srcMissing = !all.Any(r => r.Side == RecordSide.Src);
        if (srcMissing)
        {
            verdict = UpkeepVerdict.Rebuild;
        }

        if (!all.Any(r => r.Side == RecordSide.Dst))
        {
            verdict = UpkeepVerdict.Rebuild;
        }

        foreach (var (_, _, fields) in all)
        {
            if (!fields.TryGetValue(StateFields.Ep, out var text) || !long.TryParse(text, out var ep) || ep < state.Ep)
            {
                verdict = UpkeepVerdict.Rebuild;
            }
            else if (ep > state.Ep || fields.GetValueOrDefault(StateFields.Id) != state.Id)
            {
                return (UpkeepVerdict.IdentityConflict, srcMissing);
            }
            else if (state.CacheIds.Any(cid => !fields.ContainsKey("c:" + cid)))
            {
                verdict = UpkeepVerdict.Rebuild;
            }
        }

        return (verdict, srcMissing);
    }

    public Task<TransitionResult> RebuildAsync(string group, StateRecord state, SequenceOptions? sequenceOptions, CancellationToken cancellationToken) =>
        !state.Durable
            ? Task.FromResult(new TransitionResult(TransitionOutcome.StateChanged, Detail: "a transition is in flight; it is resumed, not rebuilt"))
            : sequence.RunAsync(group, StateExpectation.Holding(state.Ep, state.Id, 1), ControlUpkeep.SameState(state, null, time), sequenceOptions, cancellationToken);

    public async Task RunAsync(string group, LeaderLease lease, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.UpkeepInterval, time, cancellationToken).ConfigureAwait(false);
                if (lease.HoldsLease)
                {
                    await CheckOnceAsync(group, cancellationToken, lease).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    internal async Task CheckOnceAsync(string group, CancellationToken cancellationToken, LeaderLease? lease = null)
    {
        try
        {
            if (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is not { } state)
            {
                await claims.RefreshAsync(group, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            var (verdict, srcMissing) = await CheckAsync(group, state, cancellationToken).ConfigureAwait(false);
            switch (verdict)
            {
                case UpkeepVerdict.IdentityConflict:
                    report?.Invoke("identity_conflict", group);
                    break;
                case UpkeepVerdict.Rebuild:
                    report?.Invoke(srcMissing ? "src_records_missing" : "rebuild", group);
                    await RebuildAsync(group, state, lease is null ? null : new SequenceOptions(HoldsLease: () => lease.HoldsLease, Fence: lease.Fence), cancellationToken).ConfigureAwait(false);
                    break;
            }

            // The claim comes after the records: its OPEN restore recreates im: from ct, and the mirror follows.
            await claims.RefreshAsync(group, await store.ReadAsync(group, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            report?.Invoke("upkeep_failed", ex.Message);
        }
    }
}
