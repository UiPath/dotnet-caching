namespace UiPath.Caching.PhaseB;

/// <param name="WaitSkipped">A barrier from step 4 on, or one an operator skipped, did not confirm and the sequence went on.</param>
internal sealed record TransitionResult(TransitionOutcome Outcome, bool WaitSkipped = false, string? Detail = null);
