namespace UiPath.Caching.KeyMove;

internal static class ApprovalKinds
{
    /// <summary>The start consent also attests the release floor.</summary>
    public const string Start = "start";
    public const string StartUnsettled = "start_unsettled";
    public const string StartNoDryRun = "start_no_dry_run";
    public const string StartMemory = "start_memory";
    public const string ControlRecordsEvictable = "control_records_evictable";
    public const string Close = "close";
    public const string CloseForce = "close_force";
    public const string Release = "release";
    public const string ForceReverse = "force_reverse";
    public const string SkipLsweptWait = "skip_lswept_wait";
    public const string SkipBarrier = "skip_barrier";
    public const string Abandon = "abandon";

    /// <summary>Consent to pass the retire gate with markers of the retiring generation left; they stay as obsolete markers.</summary>
    public const string RetireMarkers = "retire_markers";

    /// <summary>The exact texts an approval needs where it lets a step go past a check; every other kind takes free text.</summary>
    public static readonly IReadOnlyDictionary<string, string> ConfirmTexts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [CloseForce] = "close without the full soak or with canary keys counted",
        [ForceReverse] = "reverse although a node does not meet the reverse gates",
        [SkipBarrier] = "go on although a replica did not confirm the barrier",
        [Abandon] = "abandon the move and start every cache of the group cold",
    };

    public static IReadOnlyList<string> All { get; } =
        [Start, StartUnsettled, StartNoDryRun, StartMemory, ControlRecordsEvictable, Close, CloseForce, Release, ForceReverse, SkipLsweptWait, SkipBarrier, Abandon, RetireMarkers];
}
