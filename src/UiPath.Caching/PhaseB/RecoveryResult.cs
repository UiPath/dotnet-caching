namespace UiPath.Caching.PhaseB;

internal sealed record RecoveryResult(RecoveryOutcome Outcome, TransitionResult? Transition = null, string? Detail = null);
