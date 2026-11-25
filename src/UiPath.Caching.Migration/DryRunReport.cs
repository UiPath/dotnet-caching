using System.Globalization;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>What a dry run found: the keys of each cache by class and size, and what a move would cost at the starting rate.</summary>
internal sealed class DryRunReport
{
    private readonly MoveSide _side;

    public DryRunReport(IReadOnlyDictionary<string, CacheInventory> caches, int units, long scanned, KeyMoveMoverOptions rates, MoveSide side)
    {
        _side = side;
        Caches = caches ?? throw new ArgumentNullException(nameof(caches));
        Units = units;
        Scanned = scanned;
        EstimatedNativeBytes = caches.Values.Sum(c => c.EstimatedNativeBytes);
        EstimatedDuration = DurationOf(Caches.Values.Sum(c => c.Probed), EstimatedNativeBytes, Math.Max(units, 1), rates ?? throw new ArgumentNullException(nameof(rates)));
    }

    /// <summary>The inventory of each cache by cache id; keys no cache claims are under the empty id.</summary>
    public IReadOnlyDictionary<string, CacheInventory> Caches { get; }

    /// <summary>Scan units that were read.</summary>
    public int Units { get; }

    /// <summary>Keys the scan returned under the connector prefix.</summary>
    public long Scanned { get; }

    public bool HasUnavailableKeys => Caches.Values.Any(c => c.Counts.ContainsKey(DryRunClasses.Unavailable));

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

    /// <summary>The report as the bounded fields of a hash: totals, and per cache its class counts, size buckets, source bytes, decoded keys and whether it was sampled.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields()
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            Field("units", Units),
            Field("scanned", Scanned),
        };
        if (_side == MoveSide.Legacy)
        {
            fields.Add(Field("estimated_native_bytes", EstimatedNativeBytes));
            fields.Add(Field("estimated_duration_ms", (long)EstimatedDuration.TotalMilliseconds));
        }

        foreach (var (cacheId, inventory) in Caches.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            AddCacheFields(fields, cacheId.Length == 0 ? "@unowned" : cacheId, inventory);
        }

        return fields;
    }

    private static void AddCacheFields(List<KeyValuePair<string, string>> fields, string id, CacheInventory inventory)
    {
        fields.AddRange(inventory.Counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => Field($"{id}:{c.Key}", c.Value)));
        for (var bucket = 0; bucket < inventory.Sizes.Count; bucket++)
        {
            fields.Add(Field(string.Create(CultureInfo.InvariantCulture, $"{id}:size:{bucket}"), inventory.Sizes[bucket]));
        }

        fields.Add(Field($"{id}:source_bytes", inventory.SourceBytes));
        fields.Add(Field($"{id}:decoded", inventory.Decoded));
        fields.Add(Field($"{id}:sampled", inventory.Sampled ? 1 : 0));
    }

    private static KeyValuePair<string, string> Field(string name, long value) => new(name, value.ToString(CultureInfo.InvariantCulture));

    private static TimeSpan DurationOf(long probed, long nativeBytes, int units, KeyMoveMoverOptions rates)
    {
        var seconds = Math.Max(probed / rates.StartKeysPerSecond, nativeBytes / rates.StartBytesPerSecond) / units;
        return TimeSpan.FromSeconds(seconds);
    }
}
