using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>The census of 6.1 on the masters: owned keys of the source side per cache, those a mover would still move, and the canary of 6.6.</summary>
/// <param name="database">The view without the connector prefix, for the life of a physical key.</param>
internal sealed class MasterCensus(MasterScanner scanner, IGroupKeySpace space, StateStore store, IDatabase database, string connectorPrefix, TimeProvider time)
{
    private const int LifeBatch = 256;

    /// <summary>The side data leaves in the state's direction: L going forward, N going back.</summary>
    public static MoveSide SourceOf(StateRecord state) => state.Direction == "rev" ? MoveSide.Native : MoveSide.Legacy;

    /// <summary>Null when the group has no state.</summary>
    public async Task<CensusResult?> RunAsync(string group, CancellationToken cancellationToken)
    {
        if (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is not { } state)
        {
            return null;
        }

        var side = SourceOf(state);
        var cacheIds = state.CacheIds;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var t3 = T3Of(state);

        // Each attempt of the scan counts afresh, so a repeated scan never adds to the counts of the one before.
        async Task<CensusResult> Count(IAsyncEnumerable<string> keys)
        {
            var residue = cacheIds.ToDictionary(c => c, _ => 0L, StringComparer.Ordinal);
            long movable = 0;
            long? canary = t3 is null ? null : 0;
            var batch = new List<(string Key, string Cache)>(LifeBatch);

            async Task FlushAsync()
            {
                var lives = await Task.WhenAll(batch.Select(b => database.KeyTimeToLiveAsync(connectorPrefix + b.Key, CommandFlags.DemandMaster))).ConfigureAwait(false);
                for (var n = 0; n < batch.Count; n++)
                {
                    var (key, cache) = batch[n];
                    if (space.IsMovable(group, side, cache, key, lives[n]))
                    {
                        movable++;
                    }

                    if (canary is not null && lives[n] is { } life && space.MaxSourceTtl(group, cache) is { } longest && life.TotalMilliseconds + (now - t3!.Value) > longest.TotalMilliseconds)
                    {
                        canary++;
                    }
                }

                batch.Clear();
            }

            await foreach (var key in keys.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (space.OwnerOf(group, cacheIds, side, key) is not { } cache)
                {
                    continue;
                }

                residue[cache]++;
                batch.Add((key, cache));
                if (batch.Count == LifeBatch)
                {
                    await FlushAsync().ConfigureAwait(false);
                }
            }

            await FlushAsync().ConfigureAwait(false);
            return new CensusResult(residue, movable, canary);
        }

        return await scanner.RunAsync(space.Patterns(group, side), Count, cancellationToken).ConfigureAwait(false);
    }

    // S3 is when every node acknowledged the S3 identity; the leader stores the time next to the acknowledgement.
    private static long? T3Of(StateRecord state) =>
        long.TryParse(state.Field(StateFields.T3At), NumberStyles.None, CultureInfo.InvariantCulture, out var at) ? at : null;
}
