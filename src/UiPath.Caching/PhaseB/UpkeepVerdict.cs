namespace UiPath.Caching.PhaseB;

internal enum UpkeepVerdict
{
    Healthy,

    /// <summary>A record or a gate is missing, or a record is below the state's <c>ep</c>: X12 <c>Rebuild</c>.</summary>
    Rebuild,

    /// <summary>A record above the state's <c>ep</c>, or at it under another id: the state rolled back or conflicts, never a Rebuild.</summary>
    IdentityConflict,

    /// <summary>S0, S5 after the sweep, or an uncommitted state: no upkeep.</summary>
    NotApplicable,
}
