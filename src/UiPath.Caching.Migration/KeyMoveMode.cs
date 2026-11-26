namespace UiPath.Caching.Migration;

/// <summary>Which forms a call of a managed cache runs under, from the rules of its group at admission.</summary>
internal enum KeyMoveMode
{
    /// <summary>The router's per-group path: not-started, an unmanaged cache, or a group that is not key move.</summary>
    Plain,

    /// <summary>Unavailable, never origin.</summary>
    Unavailable,

    /// <summary>Wait for a new snapshot, then unavailable.</summary>
    Wait,

    Ready,
    Barrier,
    Moving,
    Drained,
    Closed,

    /// <summary>A write in ready(rev): Src is N, then L is deleted under GL.</summary>
    ReverseS1,

    /// <summary>A write in moving(rev): N is deleted, then L is written under GL.</summary>
    ReverseS3,

    /// <summary>A write in drained(rev), with the forms of moving(rev).</summary>
    ReverseS4,

    /// <summary>Union N-first with no promotion: the blind rule, a reverse state, and drained with residue.</summary>
    Union,
}
