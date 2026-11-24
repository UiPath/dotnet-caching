using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace UiPath.Caching.KeyMove;

internal static class KeyMoveTelemetry
{
    public const string MeterName = "UiPath.Caching.KeyMove";

    private static readonly Meter KeyMoveMeter = new(MeterName);
    private static readonly Counter<long> MarkerOrphans = KeyMoveMeter.CreateCounter<long>("cache.keymove.marker_orphans");

    public static void CountMarkerOrphans(string group, int count)
    {
        if (count > 0 && MarkerOrphans.Enabled)
        {
            MarkerOrphans.Add(count, new TagList { { "group", group } });
        }
    }
}
