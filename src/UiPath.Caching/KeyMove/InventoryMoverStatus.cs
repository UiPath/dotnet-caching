using System.Collections.Concurrent;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>What the leader reads from the inventory: the residue and the canary, with a successful drain requiring a finished mover pass.</summary>
/// <remarks>The inventory behind the drain report is reused for <see cref="ReuseFor"/>, since a scan of every master on each 2 second step would load the servers; a failed inventory is no evidence.</remarks>
/// <param name="moverEvidence">When given, it replaces <paramref name="moverFinishedPass"/>: null for no finished pass, else the evidence the passes rest on (the identity and the unit topology), which the confirmation window is bound to.</param>
/// <param name="confirmAfter">How long after it first saw the mover's passes clean the leader waits before it takes the primary inventory that proves them (the replica lag bound); null takes it at once.</param>
/// <param name="moverFinishedPass">Whether the mover's own pass for the group finished after the acknowledgement of S3; null when there is no mover.</param>
internal sealed class InventoryMoverStatus(KeyInventory inventory, TimeProvider time, Func<string, string, CancellationToken, Task<bool>>? moverFinishedPass = null, TimeSpan? confirmAfter = null, Func<string, string, CancellationToken, Task<string?>>? moverEvidence = null) : IMoverStatus
{
    public static readonly TimeSpan ReuseFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (long At, InventoryResult? Result)> _recent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Evidence, long At)> _cleanSince = new(StringComparer.Ordinal);

    public async Task<DrainReport?> DrainAsync(string group, string direction, CancellationToken cancellationToken)
    {
        // The cheap check first: the mover's own answer costs no scan.
        string? evidence = null;
        bool moverPassed;
        if (moverEvidence is not null)
        {
            evidence = await moverEvidence(group, direction, cancellationToken).ConfigureAwait(false);
            moverPassed = evidence is not null;
        }
        else
        {
            moverPassed = moverFinishedPass is not null && await moverFinishedPass(group, direction, cancellationToken).ConfigureAwait(false);
        }

        var confirmAt = ConfirmationTime(group, direction, moverPassed, evidence);

        InventoryResult? result;
        try
        {
            result = await RecentAsync(group, confirmAt is { } at && time.GetTimestamp() >= at ? at : null, cancellationToken).ConfigureAwait(false);
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
        var confirmed = confirmAt is null || (_recent.TryGetValue(group, out var held) && held.At >= confirmAt);
        return new DrainReport(moverPassed && confirmed && result.Movable == 0 && result.InvalidMapping == 0, result.Residue, result.Canary ?? 0);
    }

    public async Task<IReadOnlyDictionary<string, long>> ResidueInventoryAsync(string group, CancellationToken cancellationToken) =>
        (await inventory.RunAsync(group, cancellationToken).ConfigureAwait(false))?.Residue ?? new Dictionary<string, long>();

    private long? ConfirmationTime(string group, string direction, bool moverPassed, string? evidence)
    {
        // Confirmation uses a primary inventory begun after the replica lag window.
        var key = group + "|" + direction;
        long? confirmAt = null;
        if (!moverPassed)
        {
            _cleanSince.TryRemove(key, out _);
        }
        else if (confirmAfter is { } wait)
        {
            // The window belongs to the evidence it was opened for: another identity or topology (a primary added or removed, a run id changed) opens it again.
            var (_, sinceAt) = _cleanSince.AddOrUpdate(key, _ => (evidence ?? string.Empty, time.GetTimestamp()), (_, held) => held.Evidence == (evidence ?? string.Empty) ? held : (evidence ?? string.Empty, time.GetTimestamp()));
            confirmAt = sinceAt + (long)(wait.TotalSeconds * time.TimestampFrequency);
        }

        return confirmAt;
    }

    private async Task<InventoryResult?> RecentAsync(string group, long? startedAfter, CancellationToken cancellationToken)
    {
        // An inventory that began before the confirming time is no confirmation, however recent it is.
        if (_recent.TryGetValue(group, out var held) && time.GetElapsedTime(held.At) < ReuseFor && (startedAfter is null || held.At >= startedAfter))
        {
            return held.Result;
        }

        var at = time.GetTimestamp();
        var fresh = await inventory.RunAsync(group, cancellationToken).ConfigureAwait(false);
        _recent[group] = (at, fresh);
        return fresh;
    }
}
