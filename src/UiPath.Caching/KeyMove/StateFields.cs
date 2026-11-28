namespace UiPath.Caching.KeyMove;

internal static class StateFields
{
    public const string State = "st";
    public const string Direction = "dir";
    public const string Ep = "ep";
    public const string Id = "id";
    public const string Durable = "dur";
    public const string NPop = "nPop";

    /// <summary>The <c>nPop</c> a control record holds (the state hash calls it <see cref="NPop"/>).</summary>
    public const string RecordNPop = "np";

    public const string Prev = "prev";
    public const string MoveId = "moveId";
    public const string Caches = "caches";
    public const string CacheSet = "cs";
    public const string AllAck = "allAck";
    public const string Swept = "lswept";
    public const string Claim = "ct";
    public const string PendToken = "pt";
    public const string PendUncertain = "ptu";
    public const string Release = "rel";
    public const string Updated = "upd";
    public const string Move = "mv";
    public const string CommitBegun = "cmt";
    public const string HoldOp = "hold:op";
    public const string HoldAuto = "hold:auto";
    public const string ResiduePrefix = "res:";
    public const string ApprovalPrefix = "appr:";
    public const string CopiedAt = "drainedAt";
    public const string DoneAt = "closedAt";
    public const string DryRun = "dry";
    public const string ColdStart = "cold";
    public const string CanaryAt = "canaryAt";
    public const string ResidueId = "resId";
    public const string T3At = "s3AckAt";

    /// <summary>Why the leader's last step took no step: its wait and hold reasons, one per line, each as <c>code:detail</c>; absent once a step is taken.</summary>
    public const string Blocked = "blocked";

    /// <summary>When the leader first saw the reasons in <see cref="Blocked"/>, in Unix milliseconds.</summary>
    public const string BlockedAt = "blockedAt";
    public const string Attested = "att";
    public const string Fg = "fg";
    public const string Rg = "rg";
    public const string PrevFg = "pfg";
    public const string PrevRg = "prg";
}
