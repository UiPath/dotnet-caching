namespace UiPath.Caching.KeyMove;

internal enum ReadRule
{
    Follow,

    /// <summary>The native key first, then the in-place key, with no promotion: the rule of a node that has no current state.</summary>
    UnionNativeFirst,
}
