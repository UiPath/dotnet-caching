namespace UiPath.Caching.Migration;

/// <summary>How fast the scan units move keys and when they must stop; one instance serves every unit of the process.</summary>
internal interface IMoveRate
{
    /// <summary>Null when the unit may go on, or the reason it must wait (<c>red</c>, <c>schedule</c>, <c>oom_backoff</c> or a memory rule).</summary>
    ValueTask<string?> PauseReasonAsync(string unit, CancellationToken cancellationToken);

    /// <summary>Charges keys and bytes against the unit's rate and the cap, and returns how long to wait before the next key is captured.</summary>
    TimeSpan Reserve(string unit, int keys, long bytes);

    /// <summary>Records bytes a unit has published and not yet paid for, on the rate and not on the runner, so a debt outlives a runner that stopped or a pass that began again.</summary>
    void Owe(string unit, long bytes);

    /// <summary>Takes the unit's debt, which the caller pays with its next reservation.</summary>
    long TakeDebt(string unit);

    /// <summary>How long is left of the last reservation of the unit at the rates now in force, so a wait in progress follows a reload; null when the rate keeps no such state and the caller counts the wait down itself.</summary>
    TimeSpan? Remaining(string unit) => null;

    /// <summary>Reports one key the unit has handled, for the mover's own measurements and the out-of-memory backoff.</summary>
    void Observed(string unit, MoveResult result, TimeSpan elapsed);
}
