namespace UiPath.Caching.PhaseB;

/// <param name="SkipBarriers">Go on when a replication barrier does not confirm after its tries, as the operator's <c>SkipBarrier</c> does; the result then says <c>WaitSkipped</c>.</param>
/// <param name="Release">The claim certificate DURABLE writes for an S0 target of a group that holds a claim; empty for none.</param>
/// <param name="SkipAtOnce">Issue the writes and issue no <c>WAIT</c>: the abandon of a transition that nothing acted on.</param>
/// <param name="WayBack">A way-back transition: a master that failed a barrier in the ledger's window is skipped at once, any other one after its tries.</param>
/// <param name="Ledger">Remembers failed barriers across transitions; required for the way-back rule.</param>
internal sealed record SequenceOptions(bool SkipBarriers = false, string Release = "", bool SkipAtOnce = false, bool WayBack = false, BarrierLedger? Ledger = null);
