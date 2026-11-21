namespace UiPath.Caching.PhaseB;

/// <param name="Flagged">The group's registration carries the Phase B flag.</param>
/// <param name="Listed">The group is in the <c>PhaseBGroups</c> setting.</param>
internal sealed record GroupRegistration(string Name, bool Flagged, bool Listed);
