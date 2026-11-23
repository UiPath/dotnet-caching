namespace UiPath.Caching.KeyMove;

/// <param name="SkipBarriers">Go on when a replication barrier does not confirm after its tries, as the operator's <c>SkipBarrier</c> does; the result then says <c>WaitSkipped</c>.</param>
/// <param name="Release">The claim certificate DURABLE writes for an S0 target of a group that holds a claim; empty for none.</param>
/// <param name="SkipAtOnce">Issue the writes and issue no <c>WAIT</c>: the abandon of a transition that nothing acted on.</param>
/// <param name="WayBack">A way-back transition: a master that failed a barrier in the ledger's window is skipped at once, any other one after its tries.</param>
/// <param name="HoldsLease">Asked before every step that writes; false stops the sequence with <c>LeaseLost</c>, so a controller that outlived its lease writes nothing more.</param>
/// <param name="Fence">The lease the state mutations of the sequence (TRANSIT, the commit marker) carry, so a controller that was paused past its lease is refused by the server.</param>
/// <param name="Ledger">Remembers failed barriers across transitions; required for the way-back rule.</param>
/// <param name="BeforeCommit">Runs after barrier 1 and its verification and before the commit begins; a result other than null ends the sequence with it (the claim open of an X1).</param>
/// <param name="AfterMarker">Checks the Dst connection proof after the commit marker and can pause the transition for retry.</param>
/// <param name="BeforeDurable">Runs after barrier 2 and before DURABLE; a result other than null ends the sequence with it (the claim certificate of a transition into S0).</param>
internal sealed record SequenceOptions(
    bool SkipBarriers = false,
    string Release = "",
    bool SkipAtOnce = false,
    bool WayBack = false,
    BarrierLedger? Ledger = null,
    Func<bool>? HoldsLease = null,
    LeaseFence? Fence = null,
    Func<CancellationToken, Task<TransitionResult?>>? BeforeCommit = null,
    Func<CancellationToken, Task<TransitionResult?>>? BeforeDurable = null,
    Func<ConnectionProof?, CancellationToken, Task<TransitionResult?>>? AfterMarker = null);
