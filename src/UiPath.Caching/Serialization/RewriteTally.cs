namespace UiPath.Caching.Serialization;

/// <summary>What a rewrite did to each value it carried: one for a string, a count per outcome for the fields of a hash.</summary>
internal readonly record struct RewriteTally(int Done, int Changed, int Lossy, int Held = 0)
{
    /// <summary>Which of the offered values were lossy, by position, so those alone are not offered again for a long while; null means all of them when <see cref="Lossy"/> is not zero.</summary>
    public IReadOnlyList<int>? LossyAt { get; init; }

    public static RewriteTally Of(RewriteOutcome outcome) => outcome switch
    {
        RewriteOutcome.Done => new(1, 0, 0),
        RewriteOutcome.Changed => new(0, 1, 0),
        RewriteOutcome.Lossy => new(0, 0, 1),
        RewriteOutcome.Held => new(0, 0, 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A job only reports what it did to a value."),
    };
}
