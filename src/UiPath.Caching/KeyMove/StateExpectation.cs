namespace UiPath.Caching.KeyMove;

/// <summary>What TRANSIT requires of the stored state: no hash, or the identity it replaces with a given commit flag (1 for a transition, 0 for an abandon). An abandon is also refused once the commit of that identity has begun (<c>cmt</c>).</summary>
internal sealed record StateExpectation(long? Ep, string? Id, int Durable, bool Abandon = false)
{
    public static StateExpectation Absent { get; } = new(null, null, 0);

    public bool IsAbsent => Ep is null;

    public static StateExpectation Holding(long ep, string id, int durable, bool abandon = false) => new(ep, id, durable, abandon);
}
