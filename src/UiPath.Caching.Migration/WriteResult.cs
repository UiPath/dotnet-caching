namespace UiPath.Caching.Migration;

/// <summary>What a write reported, and for an unknown outcome how long after sending the command can still land.</summary>
/// <param name="Outcome">What the client reported.</param>
/// <param name="SettleWithin">For <see cref="WriteOutcome.Unknown"/>, the longest the command can stay in flight; the router keeps the key pending for the longer of this and the registration's <c>PendingWindow</c>.</param>
public readonly record struct WriteResult(WriteOutcome Outcome, TimeSpan? SettleWithin = null)
{
    public static implicit operator WriteResult(WriteOutcome outcome) => FromOutcome(outcome);

    public static WriteResult FromOutcome(WriteOutcome outcome) => new(outcome);

    public static WriteResult UnknownWithin(TimeSpan settleWithin) => new(WriteOutcome.Unknown, settleWithin);
}
