using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace UiPath.Caching.Migration;

internal static class RouterTelemetry
{
    public const string MeterName = "UiPath.Caching.Migration";

    private static readonly Meter RouterMeter = new(MeterName);

    private static readonly ConcurrentDictionary<string, Counter<long>> MoveCounters = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Counter<long>> MarkerCounters = new(StringComparer.Ordinal);
    private static readonly Counter<long> LocalHit = RouterMeter.CreateCounter<long>("local_hit");
    private static readonly Counter<long> RedisHit = RouterMeter.CreateCounter<long>("redis_hit");
    private static readonly Counter<long> Miss = RouterMeter.CreateCounter<long>("miss");
    private static readonly Counter<long> FactoryRuns = RouterMeter.CreateCounter<long>("factory_runs");
    private static readonly Counter<long> Unavailable = RouterMeter.CreateCounter<long>("unavailable");
    private static readonly Counter<long> Unreadable = RouterMeter.CreateCounter<long>("unreadable");
    private static readonly Counter<long> PublishFailed = RouterMeter.CreateCounter<long>("publish_failed");
    private static readonly Counter<long> Pending = RouterMeter.CreateCounter<long>("pending");
    private static readonly Counter<long> ModeChange = RouterMeter.CreateCounter<long>("mode_change");
    private static readonly Counter<long> LeaveTimedOut = RouterMeter.CreateCounter<long>("leave_timed_out");
    private static readonly Counter<long> CleanupTimedOut = RouterMeter.CreateCounter<long>("cleanup_timed_out");
    private static readonly Counter<long> LibraryStartFailed = RouterMeter.CreateCounter<long>("library_start_failed");
    private static readonly Counter<long> Bridge = RouterMeter.CreateCounter<long>("bridge");

    public static void CountLocalHit(string cache, string group, Route route) => Add(LocalHit, cache, group, route.Path, route.Reason);

    public static void CountRedisHit(string cache, string group, RoutePath path, string reason) => Add(RedisHit, cache, group, path, reason);

    public static void CountMiss(string cache, string group, Route route) => Add(Miss, cache, group, route.Path, route.Reason);

    public static void CountFactoryRun(string cache, string group, Route route) => Add(FactoryRuns, cache, group, route.Path, route.Reason);

    public static void CountUnavailable(string cache, string group, RoutePath path, string reason) => Add(Unavailable, cache, group, path, reason);

    public static void CountUnreadable(string cache, string group, RoutePath path, string reason) => Add(Unreadable, cache, group, path, reason);

    public static void CountPublishFailed(string cache, string group, Route route) => Add(PublishFailed, cache, group, route.Path, route.Reason);

    public static void CountPending(string cache, string group, Route route) => Add(Pending, cache, group, route.Path, route.Reason);

    public static void CountModeChange(RouterMode mode, RouterModeReason reason)
    {
        if (ModeChange.Enabled)
        {
            ModeChange.Add(1, new TagList { { "mode", mode == RouterMode.Library ? "library" : "bypass_library" }, { "reason", reason.ToString() } });
        }
    }

    /// <summary>A counter of the key move data path: <c>capture_slow</c>, <c>fence_refused</c>, <c>gate_closed</c>, <c>promotion_expiring</c>, <c>identity_rerun</c>, <c>retry_exhausted</c>, <c>unreadable_source</c>.</summary>
    public static void CountMove(string counter, string cache, string group)
    {
        var made = MoveCounters.GetOrAdd(counter, name => RouterMeter.CreateCounter<long>("move_" + name));
        if (made.Enabled)
        {
            made.Add(1, new TagList { { "cache", cache }, { "group", group } });
        }
    }

    /// <summary>A counter of the supersession marker, named <c>cache.keymove.</c> and the counter: <c>unmark_dropped</c>, <c>superseded_helped</c>, <c>pending_write</c>, <c>marker_capacity</c>.</summary>
    public static void CountMarker(string counter, string cache, string group)
    {
        var made = MarkerCounters.GetOrAdd(counter, name => RouterMeter.CreateCounter<long>("cache.keymove." + name));
        if (made.Enabled)
        {
            made.Add(1, new TagList { { "cache", cache }, { "group", group } });
        }
    }

    public static void CountLibraryStartFailed() => LibraryStartFailed.Add(1);

    public static void CountBridge(string outcome, BridgeMessageKind kind)
    {
        if (Bridge.Enabled)
        {
            var kindTag = kind switch
            {
                BridgeMessageKind.Key => "key",
                BridgeMessageKind.Tag => "tag",
                _ => "clear",
            };
            Bridge.Add(1, new TagList { { "outcome", outcome }, { "kind", kindTag } });
        }
    }

    public static void CountCleanupTimedOut() => CleanupTimedOut.Add(1);

    public static void CountLeaveTimedOut() => LeaveTimedOut.Add(1);

    private static void Add(Counter<long> counter, string cache, string group, RoutePath path, string reason)
    {
        if (!counter.Enabled)
        {
            return;
        }

        counter.Add(
            1,
            new TagList
            {
                { "cache", cache },
                { "group", group },
                { "path", path == RoutePath.Library ? "library" : "source" },
                { "path_reason", reason },
            });
    }
}
