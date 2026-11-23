namespace UiPath.Caching.KeyMove;

internal enum HoldKind
{
    /// <summary><c>hold:op</c>: set by <c>Hold</c>, <c>Reassert</c> and a recreation, cleared only by <c>Resume</c>.</summary>
    Operator,

    /// <summary><c>hold:auto</c>: set by the canary and by a node without the key move token; the leader clears it when the cause is gone.</summary>
    Automatic,
}
