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
}
