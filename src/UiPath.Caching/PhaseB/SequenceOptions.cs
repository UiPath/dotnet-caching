namespace UiPath.Caching.PhaseB;

/// <param name="SkipBarriers">Go on when a replication barrier does not confirm, as the operator's <c>SkipBarrier</c> and the way back do; the result then says <c>WaitSkipped</c>.</param>
/// <param name="Release">The claim certificate DURABLE writes for an S0 target of a group that holds a claim; empty for none.</param>
internal sealed record SequenceOptions(bool SkipBarriers = false, string Release = "");
