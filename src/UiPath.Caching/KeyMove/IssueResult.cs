namespace UiPath.Caching.KeyMove;

internal enum IssueResult
{
    Done,

    /// <summary>A script refused the write, so there is nothing to confirm.</summary>
    Refused,

    Misrouted,
}
