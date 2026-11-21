namespace UiPath.Caching.PhaseB;

internal enum RemoveOutcome
{
    Removed,
    NoState,

    /// <summary>The group is mid-transition, in S1 to S4, in S5 before <c>lswept</c>, or an X11 purge is not finished.</summary>
    NotApplicable,

    /// <summary>The group holds a move claim and no way to drop it was given.</summary>
    ClaimHeld,

    ClaimDropFailed,

    /// <summary>A control record of a newer identity than the state's survives; the state is kept so the command can be repeated.</summary>
    RecordsRemain,

    /// <summary>The state moved, or the lease was lost, while the command ran.</summary>
    Refused,
}
