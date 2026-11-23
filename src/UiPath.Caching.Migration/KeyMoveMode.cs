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

    /// <summary>Union N-first with no promotion: the blind rule, a reverse state, and S4 with residue.</summary>
    Union,
}
