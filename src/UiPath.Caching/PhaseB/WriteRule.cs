namespace UiPath.Caching.PhaseB;

internal enum WriteRule
{
    /// <summary>The write forms of the state the node acts under.</summary>
    Follow,

    /// <summary>Unavailable, never origin.</summary>
    FailClosed,

    /// <summary>Wait for a new snapshot up to the remaining time of the rules, then fail closed.</summary>
    WaitThenFailClosed,
}
