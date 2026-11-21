namespace UiPath.Caching.PhaseB;

internal enum IssueResult
{
    Done,

    /// <summary>A script refused the write, so there is nothing to confirm.</summary>
    Refused,

    /// <summary>The dedicated connection sent the write to another master than the one it was issued for.</summary>
    Misrouted,
}
