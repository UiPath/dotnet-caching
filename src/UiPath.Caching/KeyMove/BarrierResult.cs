namespace UiPath.Caching.KeyMove;

internal enum BarrierResult
{
    Confirmed,

    Failed,

    /// <summary>A write may have applied in part, or its EXEC reply was lost: it was not sent again, and the caller must not send it again either.</summary>
    Uncertain,

    /// <summary>The writes were issued and no WAIT was: the master is skipped by the options or by the ledger.</summary>
    Skipped,

    /// <summary>A script refused the write, so there is nothing to confirm.</summary>
    Refused,

    /// <summary>The write went to another master; nothing was waited for and the caller issues it again on the owner.</summary>
    Misrouted,
}
