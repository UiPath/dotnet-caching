namespace UiPath.Caching.PhaseB;

/// <param name="Plan">What the step logic decided.</param>
/// <param name="Result">What the transition sequence returned, when the plan ran one.</param>
internal sealed record StepOutcome(StepPlan Plan, TransitionResult? Result);
