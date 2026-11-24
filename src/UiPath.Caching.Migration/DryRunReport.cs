using System.Globalization;

namespace UiPath.Caching.Migration;

/// <summary>What a dry run found: the keys of each cache by class and size, and what a move would cost at the starting rate.</summary>
internal sealed class DryRunReport
{
    public DryRunReport(IReadOnlyDictionary<string, CacheInventory> caches, int units, long scanned, KeyMoveMoverOptions rates)
    {
        Caches = caches ?? throw new ArgumentNullException(nameof(caches));
        Units = units;
        Scanned = scanned;
        EstimatedNativeBytes = caches.Values.Sum(c => c.EstimatedNativeBytes);
        EstimatedDuration = DurationOf(Caches.Values.Sum(c => c.Movable), EstimatedNativeBytes, Math.Max(units, 1), rates ?? throw new ArgumentNullException(nameof(rates)));
    }

    /// <summary>The inventory of each cache by cache id; keys no cache claims are under the empty id.</summary>
    public IReadOnlyDictionary<string, CacheInventory> Caches { get; }

    /// <summary>Scan units that were read.</summary>
    public int Units { get; }

    /// <summary>Keys the scan returned under the connector prefix.</summary>
    public long Scanned { get; }

    public long EstimatedNativeBytes { get; }

    /// <summary>How long the move takes at the starting rate of every unit, the slower of the keys and the bytes.</summary>
    public TimeSpan EstimatedDuration { get; }

    /// <summary>The counts per class over every cache, largest first, such as <c>movable=10,short_ttl=2</c>.</summary>
    public string Summary()
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var counts in Caches.Values.Select(c => c.Counts))
        {
            foreach (var (name, count) in counts)
            {
                totals[name] = totals.GetValueOrDefault(name) + count;
            }
        }

        return totals.Count == 0
            ? "none"
            : string.Join(',', totals.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Key}={t.Value}")));
    }

    private static TimeSpan DurationOf(long movable, long nativeBytes, int units, KeyMoveMoverOptions rates)
    {
        var seconds = Math.Max(movable / rates.StartKeysPerSecond, nativeBytes / rates.StartBytesPerSecond) / units;
        return TimeSpan.FromSeconds(seconds);
    }
}
