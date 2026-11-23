namespace UiPath.Caching.Migration;

/// <param name="Uncached">Served but not for the local tier: a promotion that was abandoned.</param>
internal readonly record struct PathRead<T>(ReadOutcome Kind, T? Value, DateTimeOffset? Expiration, bool Uncached = false)
    where T : class
{
    public static PathRead<T> Of(ReadOutcome kind) => new(kind, null, null);
}
