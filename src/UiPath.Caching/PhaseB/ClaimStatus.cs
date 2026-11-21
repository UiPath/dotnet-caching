namespace UiPath.Caching.PhaseB;

internal enum ClaimStatus
{
    /// <summary>No field of this token: released, replaced or never made.</summary>
    Absent,

    /// <summary>CLAIMPEND happened and CLAIMOPEN did not.</summary>
    Pending,

    Open,
}
