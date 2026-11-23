namespace UiPath.Caching.KeyMove;

internal enum ClaimOutcome
{
    /// <summary>The step was taken, or was already taken, and its replication wait confirmed or was skipped.</summary>
    Done,

    Refused,

    Failed,
}
