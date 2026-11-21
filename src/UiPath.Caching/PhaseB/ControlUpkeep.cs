using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The leader's check of the control records (a check, never a rewrite) and the X12 Rebuild it asks for.</summary>
internal sealed class ControlUpkeep(ControlRecordReader records, TransitionSequence sequence, StateStore store, PhaseBStateOptions options, TimeProvider time, Action<string, string>? report = null)
{
    /// <summary>S1 to S4, and S5 until <c>lswept</c> names the current id, own control records that scripts still check.</summary>
    public static bool Applies(StateRecord state) =>
        state.Durable && (state.State is "S1" or "S2" or "S3" or "S4" || (state.State == "S5" && state.Swept != state.Id));

    public async Task<UpkeepVerdict> CheckAsync(string group, StateRecord state, CancellationToken cancellationToken)
    {
        if (!Applies(state))
        {
            return UpkeepVerdict.NotApplicable;
        }

        RedisValue[] fields = [StateFields.Ep, StateFields.Id, .. state.CacheIds.Select(cid => (RedisValue)("c:" + cid))];
        var verdict = UpkeepVerdict.Healthy;
        foreach (var (_, held) in await records.ReadFieldsAsync(group, fields, cancellationToken).ConfigureAwait(false))
        {
            if (held[0].IsNull || !long.TryParse((string)held[0]!, out var ep) || ep < state.Ep)
            {
                verdict = UpkeepVerdict.Rebuild;
            }
            else if (ep > state.Ep || held[1] != state.Id)
            {
                return UpkeepVerdict.IdentityConflict;
            }
            else if (held.Skip(2).Any(g => g.IsNull))
            {
                verdict = UpkeepVerdict.Rebuild;
            }
        }

        return verdict;
    }

    /// <summary>X12: the same state under <c>ep</c> plus one and a new id, through the standard sequence. A changed managed set is allowed only in S1 and S2.</summary>
    /// <param name="newCaches">The encoded managed set to write with it, or null to keep the stored one.</param>
    public async Task<TransitionResult> RebuildAsync(string group, StateRecord state, string? newCaches, SequenceOptions? sequenceOptions, CancellationToken cancellationToken)
    {
        if (!state.Durable)
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "a transition is in flight; it is resumed, not rebuilt");
        }

        if (newCaches is not null && newCaches != state.Caches && !(state.State is "S1" or "S2" && state.Direction == "fwd"))
        {
            throw new InvalidOperationException($"The managed set is frozen outside S1 and S2: the group is in {state.State} {state.Direction}.");
        }

        var target = new StateTarget(
            state.State,
            state.Direction,
            state.NPop,
            state.Ep + 1,
            MoveScripts.NewNonce(),
            StateRecord.FormatPrev(state.State, state.Direction, state.NPop, state.Ep, state.Id),
            time.GetUtcNow().ToUnixTimeMilliseconds(),
            Caches: newCaches is null ? null : (newCaches, StateRecord.HashOfCaches(newCaches)));
        return await sequence.RunAsync(group, StateExpectation.Holding(state.Ep, state.Id, 1), target, sequenceOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every <see cref="PhaseBStateOptions.UpkeepInterval"/>, while this node holds the lease, checks the records and rebuilds when they ask for it.</summary>
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
                return;
            }

            switch (await CheckAsync(group, state, cancellationToken).ConfigureAwait(false))
            {
                case UpkeepVerdict.IdentityConflict:
                    report?.Invoke("identity_conflict", group);
                    break;
                case UpkeepVerdict.Rebuild:
                    report?.Invoke("rebuild", group);
                    await RebuildAsync(group, state, null, lease is null ? null : new SequenceOptions(HoldsLease: () => lease.HoldsLease, Fence: lease.Fence), cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            report?.Invoke("upkeep_failed", ex.Message);
        }
    }
}
