namespace UiPath.Caching.KeyMove;

internal static class KeyMoveGateIds
{
    public const string CloseAcks = "close.acks";
    public const string CloseSoak = "close.soak";
    public const string CloseCanary = "close.canary";
    public const string CloseConsent = "close.consent";
    public const string CloseNodeReadiness = "close.node_readiness";
    public const string CloseInventory = "close.inventory";
    public const string CloseViews = "close.views";
    public const string CloseInPlaceReaders = "close.in_place_readers";
    public const string StartKeyMoveSupport = "start.km_support";
    public const string StartBinding = "start.binding";
    public const string StartPins = "start.pins";
    public const string StartNodeList = "start.node_list";
    public const string StartForeignNode = "start.foreign_node";
    public const string StartReleaseFloor = "start.release_floor";
    public const string StartViewStable = "start.view_stable";
    public const string StartSnapshotFreshness = "start.snapshot_freshness";
    public const string StartDryRun = "start.dry_run";
    public const string StartMemoryHeadroom = "start.memory_headroom";
    public const string StartFormatSettled = "start.format_settled";
    public const string StartGroupListed = "start.group_listed";
    public const string StartServerPreflight = "start.server_preflight";
    public const string StartCacheSet = "start.cache_set";
    public const string StartNativeEmpty = "start.native_empty";
    public const string StartClaimAvailable = "start.claim_available";
    public const string ReverseRetire = "reverse.retire";
    public const string DrainConfirmingPass = "drain.confirming_pass";
    public const string DrainSkippedAccounted = "drain.skipped_accounted";
    public const string DrainPromotionsIdle = "drain.promotions_idle";
    public const string DrainCanaryZero = "drain.canary_zero";
    public const string DstPreflight = "dst_preflight";
    public const string DstStateMissing = "dst_state_missing";
    public const string DstUnreachable = "dst_unreachable";
    public const string DstPreflightTimeout = "dst_preflight_timeout";
    public const string DstNotMaster = "dst_not_master";
    public const string DstReplicaUnhealthy = "dst_replica_unhealthy";
    public const string SrcAdmissionPending = "src_admission_pending";
    public const string MirrorRepairPending = "mirror_repair_pending";
    public const string MirrorOpenUnavailable = "mirror_open_unavailable";
    public const string PendWaitUnavailable = "pend_wait_unavailable";
    public const string BaselineUnavailable = "baseline_unavailable";
}
