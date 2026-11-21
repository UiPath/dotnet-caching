namespace UiPath.Caching.PhaseB;

internal static class ApprovalKinds
{
    /// <summary>Consent to start; its text attests the release floor (G5).</summary>
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

    public static IReadOnlyList<string> All { get; } =
        [Start, StartUnsettled, StartNoDryRun, StartMemory, ControlRecordsEvictable, Close, CloseForce, Release, ForceReverse, SkipLsweptWait, SkipBarrier, Abandon];
}
