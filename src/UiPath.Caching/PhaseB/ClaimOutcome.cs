namespace UiPath.Caching.PhaseB;

internal enum ClaimOutcome
{
    /// <summary>The step was taken, or was already taken, and its replication wait confirmed or was skipped.</summary>
    Done,

    /// <summary>The claim refused: held by another token, closed, or another move is live.</summary>
    Refused,

    /// <summary>The claim could not be reached or is malformed; nothing may proceed on it.</summary>
    Failed,
}
