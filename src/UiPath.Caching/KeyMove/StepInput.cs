namespace UiPath.Caching.KeyMove;

/// <summary>Everything the step logic reads for one group at one instant; the expensive reads are lazy and asked for only when a step is otherwise ready.</summary>
/// <param name="ViewStable">How long the set of fresh nodes has been unchanged.</param>
/// <param name="DurZeroFor">How long the stored identity has shown <c>dur=0</c>; null when it is durable.</param>
/// <param name="Conflict">The reconciliation verdict (<c>identity_conflict</c>, <c>commit_mismatch</c>) or null.</param>
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
    Func<Task<IReadOnlyDictionary<string, long>>> ResidueInventory,
    Func<Task<RetireVerdict>> Retire);
