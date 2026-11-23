using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The inventory on the masters: owned keys of the source side per cache, those a mover would still move, and the canary.</summary>
/// <param name="database">The view without the connector prefix, for the life of a physical key.</param>
internal sealed class KeyInventory(MasterScanner scanner, IGroupKeySpace space, StateStore store, IDatabase database, string connectorPrefix, TimeProvider time, Action<KeyMoveAlert>? alert = null)
{
    private const int LifeBatch = 256;

    /// <summary>The side data leaves in the state's direction: L going forward, N going back.</summary>
    public static MoveSide SourceOf(StateRecord state) => state.Direction == "rev" ? MoveSide.Native : MoveSide.Legacy;

    /// <summary>Null when the group has no state.</summary>
    public async Task<InventoryResult?> RunAsync(string group, CancellationToken cancellationToken)
    {
        if (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is not { } state)
        {
            return null;
        }

        var side = SourceOf(state);
        var cacheIds = state.CacheIds;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var t3 = T3Of(state);

        return await scanner.RunAsync(space.Patterns(group, side), keys => CountAsync(keys, group, side, cacheIds, now, t3, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    // S3 is when every node acknowledged the S3 identity; the leader stores the time with that acknowledgement, and a time that no acknowledgement of the current identity stands behind is an older move's.
    private static long? T3Of(StateRecord state) =>
        state.AllAck == state.Id && long.TryParse(state.Field(StateFields.T3At), NumberStyles.None, CultureInfo.InvariantCulture, out var at) ? at : null;

    private async Task<InventoryResult> CountAsync(IAsyncEnumerable<string> keys, string group, MoveSide side, IReadOnlyList<string> cacheIds, long now, long? t3, CancellationToken cancellationToken)
    {
        // Each retry starts a fresh count.
        var residue = cacheIds.ToDictionary(c => c, _ => 0L, StringComparer.Ordinal);
        var totals = new AttemptTotals(t3 is null ? null : 0);
        var batch = new List<(string Key, string Cache)>(LifeBatch);
        await foreach (var key in keys.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (CountKey(group, side, key, residue, totals) is not { } cache)
            {
                continue;
            }

            batch.Add((key, cache));
            if (batch.Count == LifeBatch)
            {
                await FlushAsync(group, side, now, t3, totals, batch).ConfigureAwait(false);
            }
        }

        await FlushAsync(group, side, now, t3, totals, batch).ConfigureAwait(false);
        return new InventoryResult(residue, totals.Movable, totals.Canary, totals.Ambiguous, totals.Invalid);
    }

    private string? CountKey(string group, MoveSide side, string key, Dictionary<string, long> residue, AttemptTotals totals)
    {
        var owners = space.OwnersOf(group, side, key);
        var managed = owners.Where(o => residue.ContainsKey(o)).ToArray();
        if (managed.Length == 0)
        {
            return null;
        }

        if (owners.Count == 1)
        {
            var cache = owners[0];
            residue[cache]++;
            return cache;
        }

        // Each managed claimant counts an ambiguous key, but none can move it.
        totals.Ambiguous++;
        foreach (var owner in managed)
        {
            residue[owner]++;
        }

        alert?.Invoke(new KeyMoveAlert(group, "ambiguous_ownership", $"{key}: {string.Join(',', owners)}"));
        return null;
    }

    private async Task FlushAsync(string group, MoveSide side, long now, long? t3, AttemptTotals totals, List<(string Key, string Cache)> batch)
    {
        var lives = await Task.WhenAll(batch.Select(b => database.KeyTimeToLiveAsync(connectorPrefix + b.Key, CommandFlags.DemandMaster))).ConfigureAwait(false);
        for (var n = 0; n < batch.Count; n++)
        {
            var (key, cache) = batch[n];
            if (!space.HasValidDestination(group, side, cache, key))
            {
                totals.Invalid++;
            }

            if (space.IsMovable(group, side, cache, key, lives[n]))
            {
                totals.Movable++;
            }

            if (t3 is { } start && lives[n] is { } life && space.MaxSourceTtl(group, cache) is { } longest && life.TotalMilliseconds + (now - start) > longest.TotalMilliseconds)
            {
                totals.Canary++;
            }
        }

        batch.Clear();
    }

    private sealed class AttemptTotals(long? canary)
    {
        public long Movable { get; set; }

        public long Ambiguous { get; set; }

        public long Invalid { get; set; }

        public long? Canary { get; set; } = canary;
    }
}
