using System.Collections.Concurrent;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>What the leader reads from the inventory: the residue and the canary, with a successful drain requiring a finished mover pass.</summary>
/// <remarks>The inventory behind the drain report is reused for <see cref="ReuseFor"/>, since a scan of every master on each 2 second step would load the servers; a failed inventory is no evidence.</remarks>
/// <param name="moverFinishedPass">Whether the mover's own pass for the group finished after the acknowledgement of S3; null when there is no mover.</param>
internal sealed class InventoryMoverStatus(KeyInventory inventory, TimeProvider time, Func<string, string, CancellationToken, Task<bool>>? moverFinishedPass = null) : IMoverStatus
{
    public static readonly TimeSpan ReuseFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (long At, InventoryResult? Result)> _recent = new(StringComparer.Ordinal);

    public async Task<DrainReport?> DrainAsync(string group, string direction, CancellationToken cancellationToken)
    {
        // The cheap check first: the mover's own answer costs no scan.
        var moverPassed = moverFinishedPass is not null && await moverFinishedPass(group, direction, cancellationToken).ConfigureAwait(false);
        InventoryResult? result;
        try
        {
            result = await RecentAsync(group, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return null;
        }

        // A key two caches own is not attributable, so the inventory proves nothing about any of them.
        if (result is null or { Ambiguous: > 0 })
        {
            return null;
        }

        // Going forward the canary needs S3's time; without it the inventory is no evidence. In reverse the canary only counts.
        if (direction != "rev" && result.Canary is null)
        {
            return null;
        }

        // A key the registration maps to an invalid destination is no accepted skip class: the drain remains blocked while one exists.
        return new DrainReport(moverPassed && result.Movable == 0 && result.InvalidMapping == 0, result.Residue, result.Canary ?? 0);
    }

    public async Task<IReadOnlyDictionary<string, long>> ResidueInventoryAsync(string group, CancellationToken cancellationToken) =>
        (await inventory.RunAsync(group, cancellationToken).ConfigureAwait(false))?.Residue ?? new Dictionary<string, long>();

    private async Task<InventoryResult?> RecentAsync(string group, CancellationToken cancellationToken)
    {
        if (_recent.TryGetValue(group, out var held) && time.GetElapsedTime(held.At) < ReuseFor)
        {
            return held.Result;
        }

        var at = time.GetTimestamp();
        var fresh = await inventory.RunAsync(group, cancellationToken).ConfigureAwait(false);
        _recent[group] = (at, fresh);
        return fresh;
    }
}
