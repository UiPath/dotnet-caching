using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed class ControlUpkeep(ControlRecordReader records, TransitionSequence sequence, StateStore store, KeyMoveStateOptions options, TimeProvider time, Action<string, string>? report = null)
{
    /// <summary>ready to drained, and closed until <c>lswept</c> names the current id, own control records that scripts still check.</summary>
    public static bool Applies(StateRecord state) =>
        state.Durable && (state.State is KeyMoveStates.Ready or KeyMoveStates.Barrier or KeyMoveStates.Moving or KeyMoveStates.Drained || (state.State == KeyMoveStates.Closed && state.Swept != state.Id));

    /// <summary>The X12 target: the same state and direction under <c>ep</c> plus one and a new id, with the managed set when it changes.</summary>
    public static StateTarget SameState(StateRecord state, string? newCaches, TimeProvider time) => new(
        state.State,
        state.Direction,
        state.NPop,
        state.Ep + 1,
        MoveScripts.NewNonce(),
        StateRecord.FormatPrev(state.State, state.Direction, state.NPop, state.Ep, state.Id),
        time.GetUtcNow().ToUnixTimeMilliseconds(),
        Caches: newCaches is null ? null : (newCaches, StateRecord.HashOfCaches(newCaches)));

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
            if (held[0].IsNull || !long.TryParse((string?)held[0], out var ep) || ep < state.Ep)
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

    /// <summary>X12: the same state under <c>ep</c> plus one and a new id, through the standard sequence. A changed managed set is allowed only in ready and barrier.</summary>
    /// <param name="newCaches">The encoded managed set to write with it, or null to keep the stored one.</param>
    public async Task<TransitionResult> RebuildAsync(string group, StateRecord state, string? newCaches, SequenceOptions? sequenceOptions, CancellationToken cancellationToken)
    {
        if (!state.Durable)
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "a transition is in flight; it is resumed, not rebuilt");
        }

        if (newCaches is not null && newCaches != state.Caches && !(state.State is KeyMoveStates.Ready or KeyMoveStates.Barrier && state.Direction == "fwd"))
        {
            throw new InvalidOperationException($"The managed set is frozen outside ready and barrier: the group is in {KeyMoveStates.NameOf(state.State)} {state.Direction}.");
        }

        var target = SameState(state, newCaches, time);
        return await sequence.RunAsync(group, StateExpectation.Holding(state.Ep, state.Id, 1), target, sequenceOptions, cancellationToken).ConfigureAwait(false);
    }

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
            // Cancellation ends this background loop.
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
