using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// <c>PurgeData</c> and the owned-key census: every delete is a script that checks the control record of the key's slot, so a purge under a moved state is refused key by key,
/// and the state identity and the lease are checked again before the run and before every batch. A purge ends with a pass on the masters that finds no owned key.
/// </summary>
/// <param name="scanner">The masters, through the view that adds the connector prefix to the pattern.</param>
/// <param name="stateScripts">The scripts over the view of the state hash, which carries no connector prefix.</param>
/// <param name="dataScripts">The scripts over the view that names data keys and control records, with the connector prefix.</param>
internal sealed class GroupDataOps(MasterScanner scanner, IGroupKeySpace space, StateStore store, MoveScripts stateScripts, MoveScripts dataScripts, MoveKeyNames names, int batchSize = 128, int maxPasses = 5) : IGroupDataOps
{
    public async Task<long> CountNativeAsync(string group, CancellationToken cancellationToken)
    {
        if (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is not { } state)
        {
            return 0;
        }

        return await CountAsync(group, MoveSide.Native, state.CacheIds, cancellationToken).ConfigureAwait(false);
    }

    public Task PurgeNativeAsync(string group, DataGuard guard, CancellationToken cancellationToken) => PurgeAsync(group, MoveSide.Native, guard, cancellationToken);

    public Task PurgeLegacyAsync(string group, DataGuard guard, CancellationToken cancellationToken) => PurgeAsync(group, MoveSide.Legacy, guard, cancellationToken);

    /// <summary>The operator command: deletes the owned N keys of a group in S0; it never touches records or state.</summary>
    public async Task<PurgeOutcome> PurgeDataAsync(string group, CancellationToken cancellationToken)
    {
        if (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is not { } state)
        {
            return PurgeOutcome.NoState;
        }

        if (state is not { State: "S0", Durable: true })
        {
            return PurgeOutcome.NotApplicable;
        }

        try
        {
            await PurgeAsync(group, MoveSide.Native, new DataGuard(state.Id, null, AllowNoRecord: true), cancellationToken).ConfigureAwait(false);
            return PurgeOutcome.Purged;
        }
        catch (PurgeRefusedException)
        {
            return PurgeOutcome.Refused;
        }
    }

    private async Task<long> CountAsync(string group, MoveSide side, IReadOnlyList<string> cacheIds, CancellationToken cancellationToken)
    {
        long found = 0;
        await foreach (var key in scanner.ScanAsync(space.Patterns(group, side), cancellationToken).ConfigureAwait(false))
        {
            if (space.OwnerOf(group, cacheIds, side, key) is not null)
            {
                found++;
            }
        }

        return found;
    }

    private async Task PurgeAsync(string group, MoveSide side, DataGuard guard, CancellationToken cancellationToken)
    {
        if (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is not { } state || state.Id != guard.Id)
        {
            throw new PurgeRefusedException($"The state of '{group}' no longer holds the identity the purge was decided under.");
        }

        await EnsureAsync(group, guard).ConfigureAwait(false);
        for (var pass = 0; pass < maxPasses; pass++)
        {
            if (await DeletePassAsync(group, side, state.CacheIds, guard, cancellationToken).ConfigureAwait(false) == 0)
            {
                // A pass on the masters that found no owned key is the confirming pass.
                return;
            }
        }

        throw new PurgeRefusedException($"Owned keys of '{group}' were still found after {maxPasses} passes on the masters.");
    }

    private async Task<long> DeletePassAsync(string group, MoveSide side, IReadOnlyList<string> cacheIds, DataGuard guard, CancellationToken cancellationToken)
    {
        long seen = 0;
        var batch = new List<string>(batchSize);
        await foreach (var key in scanner.ScanAsync(space.Patterns(group, side), cancellationToken).ConfigureAwait(false))
        {
            if (space.OwnerOf(group, cacheIds, side, key) is null)
            {
                continue;
            }

            seen++;
            batch.Add(key);
            if (batch.Count == batchSize)
            {
                await DeleteBatchAsync(group, batch, guard).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await DeleteBatchAsync(group, batch, guard).ConfigureAwait(false);
        }

        return seen;
    }

    private async Task DeleteBatchAsync(string group, List<string> batch, DataGuard guard)
    {
        await EnsureAsync(group, guard).ConfigureAwait(false);
        var mode = guard.AllowNoRecord ? DeleteGuard.IdentityOrNoRecord : DeleteGuard.Identity;
        var deletes = batch.Select(key => dataScripts.GuardedDeleteAsync(key, names.ControlFor(group, key), mode, guard.Id)).ToArray();
        var results = await Task.WhenAll(deletes).ConfigureAwait(false);
        if (results.Any(r => r == MoveScripts.IdentityMismatch))
        {
            throw new PurgeRefusedException($"A key's control record of '{group}' holds another identity than the purge was decided under.");
        }
    }

    private async Task EnsureAsync(string group, DataGuard guard)
    {
        var held = await stateScripts.GuardAsync(names.State(group), guard.Id, guard.Fence).ConfigureAwait(false);
        if (held != MoveScripts.Written)
        {
            throw new PurgeRefusedException(held == MoveScripts.LeaseNotHeld ? $"The lease of '{group}' is no longer held." : $"The state of '{group}' moved.");
        }
    }
}
