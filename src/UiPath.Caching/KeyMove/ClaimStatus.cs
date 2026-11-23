namespace UiPath.Caching.KeyMove;

internal enum ClaimStatus
{
    /// <summary>No field of this token: released, replaced or never made.</summary>
    Absent,

    Pending,

    Open,
}
