namespace UiPath.Caching.Migration;

/// <param name="Uncached">Served but not for the local tier: a promotion that was abandoned.</param>
internal readonly record struct PathRead<T>(ReadOutcome Kind, T? Value, DateTimeOffset? Expiration, bool Uncached = false)
    where T : class
{
    /// <summary>A miss that is the answer: the entry holds a cached null, so a union read stops there and never falls back to an older value.</summary>
    public bool Stops { get; init; }

    /// <summary>A miss that a union read may still answer from the next side.</summary>
    public bool FallsThrough => Kind == ReadOutcome.Absent && !Stops;

    public static PathRead<T> Of(ReadOutcome kind) => new(kind, null, null);
}
