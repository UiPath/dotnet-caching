namespace UiPath.Caching.KeyMove;

internal sealed record RecoveryResult(RecoveryOutcome Outcome, TransitionResult? Transition = null, string? Detail = null);
