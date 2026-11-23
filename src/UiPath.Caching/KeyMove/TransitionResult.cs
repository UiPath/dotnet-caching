namespace UiPath.Caching.KeyMove;

/// <param name="WaitSkipped">A barrier from the commit on, or one an operator skipped, did not confirm and the sequence went on.</param>
internal sealed record TransitionResult(TransitionOutcome Outcome, bool WaitSkipped = false, string? Detail = null);
