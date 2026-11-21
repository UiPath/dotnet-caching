namespace UiPath.Caching.PhaseB;

/// <summary>What TRANSIT requires of the stored state: no hash, or the identity it replaces with a given commit flag (1 for a transition, 0 for an abandon).</summary>
internal sealed record StateExpectation(long? Ep, string? Id, int Durable)
{
    public static StateExpectation Absent { get; } = new(null, null, 0);

    public bool IsAbsent => Ep is null;

    public static StateExpectation Holding(long ep, string id, int durable) => new(ep, id, durable);
}
