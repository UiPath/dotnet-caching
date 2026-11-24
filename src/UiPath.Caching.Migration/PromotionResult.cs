namespace UiPath.Caching.Migration;

/// <param name="Read">What a read-through serves for this end; for the mover it is not used.</param>
/// <param name="Readmitted">For the identity kinds, whether the unit was moved to the identity now in force.</param>
/// <param name="Reason">For <see cref="PromotionKind.Abandoned"/>, the counter-style reason.</param>
/// <param name="Bytes">For a key published to N, the bytes of its encoding.</param>
internal readonly record struct PromotionResult<T>(PromotionKind Kind, PathRead<T> Read = default, bool Readmitted = false, string? Reason = null, long Bytes = 0)
    where T : class
{
    public static PromotionResult<T> Of(PromotionKind kind, PathRead<T> read = default) => new(kind, read);

    public static PromotionResult<T> Of(PromotionKind kind, ReadOutcome outcome) => new(kind, PathRead<T>.Of(outcome));
}
