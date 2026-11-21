using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// Clear of one cache in S1 to S4 (3.6): the lock <c>clrlock</c>, the gate of the cache closed on every slot, the sweep, a check that no transition replaced the gates, and the reopen that
/// only reopens this Clear's own close. In S0 and S5, and for a group with no state, it is the plain sweep with the two events.
/// </summary>
/// <param name="stateScripts">The scripts over the view of the state hash, which holds the lock.</param>
/// <param name="controlScripts">The scripts over the view that names control records.</param>
internal sealed class ClearCoordinator(StateStore store, MoveScripts stateScripts, MoveScripts controlScripts, ControlRecordReader records, MoveKeyNames names, PhaseBStateOptions options, TimeProvider time)
{
    private const int Batch = 512;

    public async Task<ClearOutcome> ClearAsync(string group, string cacheId, ClearHooks hooks, CancellationToken cancellationToken)
    {
        MoveKeyNames.ValidateName(cacheId, nameof(cacheId));
        var state = await store.ReadAsync(group, cancellationToken).ConfigureAwait(false);
        if (state is { Durable: false })
        {
            return ClearOutcome.Unavailable;
        }

        if (state is null or { State: "S0" or "S5" })
        {
            await hooks.PublishFirst(cancellationToken).ConfigureAwait(false);
            await hooks.Sweep(cancellationToken).ConfigureAwait(false);
            await hooks.PublishSecond(cancellationToken).ConfigureAwait(false);
            return ClearOutcome.Cleared;
        }

        var lockKey = names.ClearLock(group, cacheId);
        var token = MoveScripts.NewNonce();
        if (await stateScripts.LeaseAsync(lockKey, token, (long)options.ClearLockTtl.TotalMilliseconds).ConfigureAwait(false) == MoveScripts.LeaseHeldByAnother)
        {
            return ClearOutcome.Unavailable;
        }

        using var sweepStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lost = false;
        var renewing = Task.Run(() => RenewAsync(lockKey, token, sweepStop, () => lost = true), CancellationToken.None);
        try
        {
            return await ClearGatedAsync(group, cacheId, state, hooks, () => Volatile.Read(ref lost), sweepStop.Token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await sweepStop.CancelAsync().ConfigureAwait(false);
            await renewing.ConfigureAwait(false);
            await stateScripts.LeaseReleaseAsync(lockKey, token).ConfigureAwait(false);
        }
    }

    private async Task<ClearOutcome> ClearGatedAsync(string group, string cacheId, StateRecord state, ClearHooks hooks, Func<bool> lockLost, CancellationToken sweepToken, CancellationToken cancellationToken)
    {
        await hooks.PublishFirst(cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < options.ClearRetries; attempt++)
        {
            if (attempt > 0)
            {
                // A transition that ran during the sweep replaced the gates; the next close is made under the identity now in force.
                if (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is not { Durable: true, State: "S1" or "S2" or "S3" or "S4" } reread)
                {
                    return ClearOutcome.Unavailable;
                }

                state = reread;
            }

            var closed = "CLOSED:" + MoveScripts.NewNonce();
            if (!await CloseAsync(group, state.Id, cacheId, closed).ConfigureAwait(false))
            {
                await ReopenAsync(group, state.Id, cacheId, closed).ConfigureAwait(false);
                return ClearOutcome.Unavailable;
            }

            try
            {
                await hooks.Sweep(sweepToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lockLost() && !cancellationToken.IsCancellationRequested)
            {
                await ReopenAsync(group, state.Id, cacheId, closed).ConfigureAwait(false);
                return ClearOutcome.Failed;
            }
            catch
            {
                await ReopenAsync(group, state.Id, cacheId, closed).ConfigureAwait(false);
                throw;
            }

            if (lockLost())
            {
                await ReopenAsync(group, state.Id, cacheId, closed).ConfigureAwait(false);
                return ClearOutcome.Failed;
            }

            if (await HoldsAsync(group, cacheId, closed, cancellationToken).ConfigureAwait(false))
            {
                if (!await ReopenAsync(group, state.Id, cacheId, closed).ConfigureAwait(false))
                {
                    return ClearOutcome.Failed;
                }

                await hooks.PublishSecond(cancellationToken).ConfigureAwait(false);
                return ClearOutcome.Cleared;
            }
        }

        // The last close was lost to a transition, so there is nothing of ours left to reopen; the next attempt would sweep again.
        return ClearOutcome.Failed;
    }

    private async Task<bool> CloseAsync(string group, string id, string cacheId, string closed)
    {
        try
        {
            foreach (var chunk in names.SlotSet().Chunk(Batch))
            {
                var writes = await Task.WhenAll(chunk.Select(slot => controlScripts.GateAsync(names.Control(group, slot), id, cacheId, closed))).ConfigureAwait(false);
                if (writes.Any(w => w != MoveScripts.Written))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return false;
        }
    }

    private async Task<bool> HoldsAsync(string group, string cacheId, string closed, CancellationToken cancellationToken) =>
        (await records.ReadFieldsAsync(group, ["c:" + cacheId], cancellationToken).ConfigureAwait(false)).All(r => r.Values[0] == closed);

    // Reopens only the gates still holding this Clear's close, on every slot, so another Clear's close is never undone. True when no write failed.
    private async Task<bool> ReopenAsync(string group, string id, string cacheId, string closed)
    {
        var open = "OPEN:" + MoveScripts.NewNonce();
        var ok = true;
        foreach (var chunk in names.SlotSet().Chunk(Batch))
        {
            try
            {
                var writes = await Task.WhenAll(chunk.Select(slot => controlScripts.GateAsync(names.Control(group, slot), id, cacheId, open, closed))).ConfigureAwait(false);
                ok &= writes.All(w => w is MoveScripts.Written or MoveScripts.ExpectationFailed or MoveScripts.IdentityMismatch);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                ok = false;
            }
        }

        return ok;
    }

    private async Task RenewAsync(RedisKey lockKey, string token, CancellationTokenSource stop, Action markLost)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.ClearLockRenewInterval, time, stop.Token).ConfigureAwait(false);
                if (await stateScripts.LeaseAsync(lockKey, token, (long)options.ClearLockTtl.TotalMilliseconds).ConfigureAwait(false) == MoveScripts.LeaseHeldByAnother)
                {
                    markLost();
                    await stop.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The Clear ended.
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // A lock that cannot be renewed is lost: the sweep stops and reopens only what it closed.
            markLost();
            await stop.CancelAsync().ConfigureAwait(false);
        }
    }
}
