using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace UiPath.Caching.KeyMove;

internal static class KeyMoveTelemetry
{
    public const string MeterName = "UiPath.Caching.KeyMove";

    private static readonly Meter KeyMoveMeter = new(MeterName);
    private static readonly Counter<long> MarkerOrphans = KeyMoveMeter.CreateCounter<long>("cache.keymove.marker_orphans");
    private static readonly Counter<long> GateOverrides = KeyMoveMeter.CreateCounter<long>("cache.keymove.gate_overrides");
    private static readonly Counter<long> CanaryKeys = KeyMoveMeter.CreateCounter<long>("cache.keymove.canary_keys");
    private static readonly Counter<long> WrittenAfterMoving = KeyMoveMeter.CreateCounter<long>("cache.keymove.written_after_moving");
    private static readonly ConcurrentDictionary<string, InventorySnapshot> LastInventory = new(StringComparer.Ordinal);

    // The gauges hold the last inventory of each group; the instruments are kept alive by the meter.
    private static readonly ObservableGauge<long> WrittenAfterMovingGauge = KeyMoveMeter.CreateObservableGauge("cache.keymove.inventory_written_after_moving", () => Measurements(static snapshot => snapshot.Canary));
    private static readonly ObservableGauge<long> ExpectedUnmappedGauge = KeyMoveMeter.CreateObservableGauge("cache.keymove.inventory_expected_unmapped", () => Measurements(static snapshot => snapshot.ExpectedUnmapped));

    /// <summary>A step taken with a gate that a stored forcing approval overrode.</summary>
    public static void CountGateOverride(string group, string gate)
    {
        if (GateOverrides.Enabled)
        {
            GateOverrides.Add(1, new TagList { { "group", group }, { "gate", gate } });
        }
    }

    /// <summary>The keys an inventory pass counted as written after every node stopped writing the source side.</summary>
    public static void CountCanaryKeys(string group, long count)
    {
        if (count > 0 && CanaryKeys.Enabled)
        {
            CanaryKeys.Add(count, new TagList { { "group", group } });
        }
    }

    public static void CountMarkerOrphans(string group, int count)
    {
        if (count > 0 && MarkerOrphans.Enabled)
        {
            MarkerOrphans.Add(count, new TagList { { "group", group } });
        }
    }

    /// <summary>Counts observations of source writes after moving. A key can be observed again in a later pass.</summary>
    public static void CountWrittenAfterMoving(string cache, string group)
    {
        if (WrittenAfterMoving.Enabled)
        {
            WrittenAfterMoving.Add(1, new TagList { { "cache", cache }, { "group", group } });
        }
    }

    /// <summary>Stores the last inventory's values for the gauges; the canary is null when moving is unknown.</summary>
    public static void RecordInventory(string group, long? writtenAfterMoving, long expectedUnmapped)
    {
        _ = WrittenAfterMovingGauge;
        _ = ExpectedUnmappedGauge;
        LastInventory[group] = new InventorySnapshot(writtenAfterMoving, expectedUnmapped);
    }

    private static IEnumerable<Measurement<long>> Measurements(Func<InventorySnapshot, long?> valueOf)
    {
        foreach (var (group, snapshot) in LastInventory)
        {
            if (valueOf(snapshot) is { } value)
            {
                yield return new Measurement<long>(value, new KeyValuePair<string, object?>("group", group));
            }
        }
    }

    private readonly record struct InventorySnapshot(long? Canary, long ExpectedUnmapped);
}
