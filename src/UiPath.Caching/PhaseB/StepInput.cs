namespace UiPath.Caching.PhaseB;

/// <summary>Everything the step logic reads for one group at one instant; the expensive reads are lazy and asked for only when a step is otherwise ready.</summary>
/// <param name="Fresh">The adverts younger than <c>AdvertFresh</c>.</param>
/// <param name="ViewStable">How long the set of fresh nodes has been unchanged.</param>
/// <param name="DurZeroFor">How long the stored identity has shown <c>dur=0</c>; null when it is durable.</param>
/// <param name="Conflict">The reconciliation verdict (<c>identity_conflict</c>, <c>commit_mismatch</c>) or null.</param>
/// <param name="StaleAfter">The snapshot age beyond which a node is stale (G7).</param>
/// <param name="Preflight">Step 0 and the protocol half of G12, for the approval state of this group.</param>
internal sealed record StepInput(
    string Group,
    StateRecord State,
    IReadOnlyList<NodeAdvert> Fresh,
    FlagVerdict Flag,
    TimeSpan ViewStable,
    DateTimeOffset Now,
    TimeSpan? DurZeroFor,
    string? Conflict,
    string? OwnScope,
    TimeSpan StaleAfter,
    IGroupFacts Facts,
    Func<bool, Task<PreflightResult>> Preflight,
    Func<Task<long>> NativeCount,
    Func<Task<DrainReport?>> Drain,
    Func<Task<IReadOnlyDictionary<string, long>>> ResidueCensus);
