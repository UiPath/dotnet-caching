namespace UiPath.Caching.Migration;

/// <param name="Code">What the script answered; -1 for a changed identity, 0 or more for committed.</param>
/// <param name="Failed">Set when the step got no answer: not sent, or unknown.</param>
internal readonly record struct StepSent(long Code, WriteOutcome? Failed = null)
{
    public static StepSent NotSent => new(0, WriteOutcome.NotSent);

    public static StepSent Unknown => new(0, WriteOutcome.Unknown);
}
