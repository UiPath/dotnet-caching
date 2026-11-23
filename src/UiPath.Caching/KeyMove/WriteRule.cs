namespace UiPath.Caching.KeyMove;

internal enum WriteRule
{
    Follow,

    /// <summary>Unavailable, never origin.</summary>
    FailClosed,

    /// <summary>Wait for a new snapshot up to the remaining time of the rules, then fail closed.</summary>
    WaitThenFailClosed,
}
