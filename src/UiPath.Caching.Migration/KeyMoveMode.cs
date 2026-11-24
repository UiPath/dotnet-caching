namespace UiPath.Caching.Migration;

/// <summary>Which forms a call of a managed cache runs under, from the rules of its group at admission.</summary>
internal enum KeyMoveMode
{
    /// <summary>The router's per-group path: S0, an unmanaged cache, or a group that is not key move.</summary>
    Plain,

    /// <summary>Unavailable, never origin.</summary>
    Closed,

    /// <summary>Wait for a new snapshot, then closed.</summary>
    Wait,

    S1,
    S2,
    S3,
    S4,
    S5,

    /// <summary>A write in S1(rev): Src is N, then L is deleted under GL.</summary>
    ReverseS1,

    /// <summary>A write in S3(rev): N is deleted, then L is written under GL.</summary>
    ReverseS3,

    /// <summary>A write in S4(rev), with the forms of S3(rev).</summary>
    ReverseS4,

    /// <summary>Union N-first with no promotion: the blind rule, a reverse state, and S4 with residue.</summary>
    Union,
}
