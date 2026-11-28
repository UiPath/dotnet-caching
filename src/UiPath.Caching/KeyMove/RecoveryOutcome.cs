namespace UiPath.Caching.KeyMove;

internal enum RecoveryOutcome
{
    Done,

    StateExists,

    /// <summary>No fresh node advertises an identity: <c>listed_group_unrecoverable</c>, and only <c>Reassert</c> helps.</summary>
    NoPeers,

    PeersDisagree,

    /// <summary>The state changed while the evidence was collected, or is newer than the records: nothing is written over it.</summary>
    StateChanged,

    RecordsDisagree,

    /// <summary>ready to closed with no surviving record: the managed set is unknown, so the operator must name it.</summary>
    NeedsReassert,

    RecordsDisagreeOnDirection,

    /// <summary>No control record exists and the operator named no state or no caches.</summary>
    NoRecords,
}
