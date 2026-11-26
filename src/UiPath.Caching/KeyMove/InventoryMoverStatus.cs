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

    private readonly ConcurrentDictionary<string, InventoryScan> _recent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Group, string Direction, string StateId), (string Evidence, long At)> _cleanSince = new();

    public async Task<DrainReport?> DrainAsync(string group, StateRecord state, CancellationToken cancellationToken)
    {
        var direction = state.Direction;

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

        var confirmAt = ConfirmationTime(group, state, moverPassed, evidence);

        InventoryScan scan;
        try
        {
            scan = await RecentAsync(group, state, confirmAt is { } at && time.GetTimestamp() >= at ? at : null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return null;
        }

        // A key two caches own is not attributable, so the inventory proves nothing about any of them.
        if (scan.Result is not { Ambiguous: 0 } result)
        {
            return null;
        }

        // Going forward the canary needs S3's time; without it the inventory is no evidence. In reverse the canary only counts.
        if (direction != "rev" && result.Canary is null)
        {
            return null;
        }

        // A key the registration maps to an invalid destination is no accepted skip class: the drain remains blocked while one exists.
        var confirmed = confirmAt is null || scan.At >= confirmAt;
        return new DrainReport(moverPassed && confirmed && result.Movable == 0 && result.InvalidMapping == 0, result.Residue, result.Canary ?? 0);
    }

    public async Task<IReadOnlyDictionary<string, long>> ResidueInventoryAsync(string group, CancellationToken cancellationToken) =>
        (await inventory.RunAsync(group, cancellationToken).ConfigureAwait(false))?.Residue ?? new Dictionary<string, long>();

    private long? ConfirmationTime(string group, StateRecord state, bool moverPassed, string? evidence)
    {
        // Confirmation uses a primary inventory begun after the replica lag window.
        var key = (group, state.Direction, state.Id);
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

    private async Task<InventoryScan> RecentAsync(string group, StateRecord state, long? startedAfter, CancellationToken cancellationToken)
    {
        // An inventory that began before the confirming time is no confirmation, however recent it is.
        var key = (state.Direction, state.Id, state.S3AckedAt);
        if (_recent.TryGetValue(group, out var held) && held.Key == key && time.GetElapsedTime(held.At) < ReuseFor && (startedAfter is null || held.At >= startedAfter))
        {
            return held;
        }

        var at = time.GetTimestamp();
        var result = await inventory.RunAsync(group, state, cancellationToken).ConfigureAwait(false);
        var scan = new InventoryScan(key, at, result);
        _recent[group] = scan;
        return scan;
    }

    private readonly record struct InventoryScan((string Direction, string StateId, long? S3AckedAt) Key, long At, InventoryResult? Result);
}
