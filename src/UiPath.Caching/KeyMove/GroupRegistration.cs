namespace UiPath.Caching.KeyMove;

/// <param name="Flagged">The group's registration carries the key move flag.</param>
/// <param name="Listed">The group is in the <c>KeyMoveGroups</c> setting.</param>
internal sealed record GroupRegistration(string Name, bool Flagged, bool Listed);
