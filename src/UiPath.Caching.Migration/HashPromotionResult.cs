namespace UiPath.Caching.Migration;

/// <param name="Read">What a read-through hands back; Absent for a promotion that only ensures.</param>
internal readonly record struct HashPromotionResult<T>(HashPromotionStatus Status, HashRead<T> Read)
    where T : class
{
    public static HashPromotionResult<T> Of(HashPromotionStatus status, ReadOutcome kind = ReadOutcome.Absent) => new(status, HashRead<T>.Of(kind));

    public static HashPromotionResult<T> Of(HashPromotionStatus status, HashRead<T> read) => new(status, read);
}
