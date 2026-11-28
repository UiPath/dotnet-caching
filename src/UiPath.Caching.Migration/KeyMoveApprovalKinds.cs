using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The kinds of approval a key move group takes.</summary>
public static class KeyMoveApprovalKinds
{
    /// <summary>Consents to the start of the move; it also attests the release floor.</summary>
    public const string Start = ApprovalKinds.Start;

    /// <summary>Starts although the node list changed within the upgrade hold.</summary>
    public const string StartUnsettled = ApprovalKinds.StartUnsettled;

    /// <summary>Starts with no recent dry run.</summary>
    public const string StartNoDryRun = ApprovalKinds.StartNoDryRun;

    /// <summary>Starts although no attestation covers the memory the dry run estimated.</summary>
    public const string StartMemory = ApprovalKinds.StartMemory;

    /// <summary>Accepts an eviction policy under which the move's control records may be evicted.</summary>
    public const string ControlRecordsEvictable = ApprovalKinds.ControlRecordsEvictable;

    /// <summary>Consents to Close and acknowledges the residue the census counted.</summary>
    public const string Close = ApprovalKinds.Close;

    /// <summary>Closes before the soak has passed, or with keys counted as written after the switch.</summary>
    public const string CloseForce = ApprovalKinds.CloseForce;

    /// <summary>Ends a step back: acknowledges the native residue, which is then swept.</summary>
    public const string Release = ApprovalKinds.Release;

    /// <summary>Steps back although a node does not meet the gates of the step back.</summary>
    public const string ForceReverse = ApprovalKinds.ForceReverse;

    /// <summary>Records the done state without waiting for a replica that does not confirm the sweep.</summary>
    public const string SkipSweptWait = ApprovalKinds.SkipLsweptWait;

    /// <summary>Goes on past a transition whose barrier a replica did not confirm.</summary>
    public const string SkipBarrier = ApprovalKinds.SkipBarrier;

    /// <summary>Abandons a done move and starts every cache of the group cold; only for caches that accept a refill.</summary>
    public const string Abandon = ApprovalKinds.Abandon;

    /// <summary>Removes the old markers before a step back may go on.</summary>
    public const string RetireMarkers = ApprovalKinds.RetireMarkers;

    /// <summary>The exact text <paramref name="kind"/> needs as its confirmation, or null when it takes free text.</summary>
    public static string? ConfirmTextOf(string kind) =>
        kind == StartUnsettled ? new StageDriverOptions().UpgradeHoldOverrideText : ApprovalKinds.ConfirmTexts.GetValueOrDefault(kind);
}
