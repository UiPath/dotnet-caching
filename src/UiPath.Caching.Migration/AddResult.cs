namespace UiPath.Caching.Migration;

/// <summary>What a conditional create reported, and for an unknown outcome how long after sending the command can still land.</summary>
/// <param name="Outcome">What the client reported.</param>
/// <param name="SettleWithin">For <see cref="AddOutcome.Unknown"/>, the longest the command can stay in flight.</param>
public readonly record struct AddResult(AddOutcome Outcome, TimeSpan? SettleWithin = null)
{
    public static implicit operator AddResult(AddOutcome outcome) => FromOutcome(outcome);

    public static AddResult FromOutcome(AddOutcome outcome) => new(outcome);

    public static AddResult UnknownWithin(TimeSpan settleWithin) => new(AddOutcome.Unknown, settleWithin);
}
