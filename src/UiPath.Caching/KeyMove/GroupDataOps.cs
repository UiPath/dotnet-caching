using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary><c>PurgeData</c> and the owned-key inventory, every delete checked by the control record of its key's slot.</summary>
/// <remarks>The state identity and the lease are checked again before the run and before each batch, and a purge ends with a pass on the masters that finds no owned key.</remarks>
/// <param name="scanner">The masters, through the view that adds the connector prefix to the pattern.</param>
/// <param name="stateScripts">The scripts over the view of the state hash, which carries no connector prefix.</param>
/// <param name="dataScripts">The scripts over the view that names data keys and control records, with the connector prefix.</param>
internal sealed class GroupDataOps(MasterScanner scanner, IGroupKeySpace space, StateStore store, MoveScripts stateScripts, MoveScripts dataScripts, MoveKeyNames names, int batchSize = 128, int maxPasses = 5, Action<KeyMoveAlert>? alert = null) : IGroupDataOps
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

    /// <summary>The operator command: deletes the owned N keys of a group in off; it never touches records or state.</summary>
    public async Task<PurgeOutcome> PurgeDataAsync(string group, CancellationToken cancellationToken)
    {
        if (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is not { } state)
        {
            return PurgeOutcome.NoState;
        }

        if (state is not { State: KeyMoveStates.Off, Durable: true })
        {
            return PurgeOutcome.NotApplicable;
        }

        try
        {
            // A missing record is repaired, not purged around, unless the group provably never started: no X1 ever stamped moveId.
            await PurgeAsync(group, MoveSide.Native, new DataGuard(state.Id, null, AllowNoRecord: state.MoveId is null), cancellationToken).ConfigureAwait(false);
            return PurgeOutcome.Purged;
        }
        catch (PurgeRefusedException)
        {
            return PurgeOutcome.Refused;
        }
    }

    private static List<string> Managed(IReadOnlyList<string> owners, IReadOnlyList<string> cacheIds) =>
        [.. owners.Where(o => cacheIds.Contains(o, StringComparer.Ordinal))];

    private Task<long> CountAsync(string group, MoveSide side, IReadOnlyList<string> cacheIds, CancellationToken cancellationToken)
    {
        async Task<long> Count(IAsyncEnumerable<string> keys)
        {
            long found = 0;
            await foreach (var key in keys.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                // A key a managed cache owns is counted as present, and one that two caches claim, managed or not, is reported, so the native inventory never passes over it.
                var owners = space.OwnersOf(group, side, key);
                if (Managed(owners, cacheIds).Count > 0)
                {
                    found++;
                    Report(group, key, owners);
                }
            }

            return found;
        }

        return scanner.RunAsync(space.Patterns(group, side), Count, cancellationToken);
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
            var seen = await DeletePassAsync(group, side, state.CacheIds, guard, cancellationToken).ConfigureAwait(false);

            // N must end empty, so a master pass finding no owned key confirms it. L serves the router's per-group path again once nodes acknowledge off.
            // AcceptRefill covers data a refill can rewrite, so the first complete pass that deleted everything it saw is the last.
            if (seen == 0 || side == MoveSide.Legacy)
            {
                return;
            }
        }

        throw new PurgeRefusedException($"Owned keys of '{group}' were still found after {maxPasses} passes on the masters.");
    }

    // A repeated attempt deletes the same keys again, which is harmless, and counts only the attempt that stood.
    private Task<long> DeletePassAsync(string group, MoveSide side, IReadOnlyList<string> cacheIds, DataGuard guard, CancellationToken cancellationToken)
    {
        async Task<long> Pass(IAsyncEnumerable<string> keys)
        {
            long seen = 0;
            var batch = new List<string>(batchSize);
            string? ambiguous = null;
            await foreach (var key in keys.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var owners = space.OwnersOf(group, side, key);
                if (Managed(owners, cacheIds).Count == 0)
                {
                    continue;
                }

                seen++;
                if (owners.Count > 1)
                {
                    // Not deleted: which cache's data it is cannot be told.
                    Report(group, key, owners);
                    ambiguous ??= $"'{key}' is owned by {string.Join(" and ", owners)}";
                    continue;
                }

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

            return ambiguous is null ? seen : throw new PurgeRefusedException($"The key {ambiguous}, so it was left in place and the purge cannot finish.");
        }

        return scanner.RunAsync(space.Patterns(group, side), Pass, cancellationToken);
    }

    private void Report(string group, string key, IReadOnlyList<string> owners)
    {
        if (owners.Count > 1)
        {
            alert?.Invoke(new KeyMoveAlert(group, "ambiguous_ownership", $"{key}: {string.Join(',', owners)}"));
        }
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
        var held = await stateScripts.GuardAsync(names.State(group), guard.Id, guard.Fence, guard.AllowNoRecord).ConfigureAwait(false);
        if (held != MoveScripts.Written)
        {
            throw new PurgeRefusedException(held switch
            {
                MoveScripts.LeaseNotHeld => $"The lease of '{group}' is no longer held.",
                MoveScripts.GroupStarted => $"The group '{group}' has started a move, so a missing control record is not proof of an empty group.",
                _ => $"The state of '{group}' moved.",
            });
        }
    }
}
