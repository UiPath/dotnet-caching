namespace UiPath.Caching.PhaseB;

internal enum RecoveryOutcome
{
    /// <summary>The write sequence ran; its result says whether it completed.</summary>
    Done,

    /// <summary>The state hash exists, so nothing is recreated.</summary>
    StateExists,

    /// <summary>No fresh node advertises an identity: <c>listed_group_unrecoverable</c>, and only <c>Reassert</c> helps.</summary>
    NoPeers,

    PeersDisagree,

    /// <summary>The state changed while the evidence was collected, or is newer than the records: nothing is written over it.</summary>
    StateChanged,

    /// <summary>The sampled control record is not the identity the peers run.</summary>
    RecordsDisagree,

    /// <summary>S1 to S5 with no surviving record: the managed set is unknown, so the operator must name it.</summary>
    NeedsReassert,

    /// <summary>Committed records at one <c>ep</c> disagree on state or direction.</summary>
    RecordsDisagreeOnDirection,

    /// <summary>No control record exists and the operator named no state or no caches.</summary>
    NoRecords,
}
