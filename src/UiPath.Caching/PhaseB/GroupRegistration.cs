namespace UiPath.Caching.PhaseB;

/// <param name="Flagged">The group's registration carries the Phase B flag.</param>
/// <param name="Listed">The group is in the <c>PhaseBGroups</c> setting.</param>
/// <param name="CacheIds">The cache ids the node registered for the group; null when the node does not say, which turns off the managed-set rule.</param>
internal sealed record GroupRegistration(string Name, bool Flagged, bool Listed, IReadOnlyList<string>? CacheIds = null);
