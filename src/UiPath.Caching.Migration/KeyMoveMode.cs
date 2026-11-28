namespace UiPath.Caching.Migration;

/// <summary>Which forms a call of a managed cache runs under, from the rules of its group at admission.</summary>
internal enum KeyMoveMode
{
    /// <summary>The router's per-group path: off, an unmanaged cache, or a group that is not key move.</summary>
    Plain,

    /// <summary>Unavailable, never origin.</summary>
    Unavailable,

    /// <summary>Wait for a new snapshot, then unavailable.</summary>
    Wait,

    Preparing,
    Switching,
    Copying,
    Copied,
    Done,

    /// <summary>A write in returning: Src is N, then L is deleted under GL.</summary>
    ReverseS1,

    /// <summary>A write in copying-back: N is deleted, then L is written under GL.</summary>
    ReverseS3,

    /// <summary>A write in copied-back, with the forms of copying-back.</summary>
    ReverseS4,

    /// <summary>Union N-first with no promotion: the blind rule, a reverse state, and copied with residue.</summary>
    Union,
}
