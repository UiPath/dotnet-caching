namespace UiPath.Caching.PhaseB;

internal enum ReadRule
{
    /// <summary>The reads of the state the node acts under.</summary>
    Follow,

    /// <summary>The native key first, then the in-place key, with no promotion: the rule of a node that has no current state.</summary>
    UnionNativeFirst,
}
