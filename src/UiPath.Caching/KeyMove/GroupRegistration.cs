namespace UiPath.Caching.KeyMove;

/// <param name="Flagged">The group's registration carries the key move flag.</param>
/// <param name="Listed">The group is in the <c>KeyMoveGroups</c> setting.</param>
/// <param name="InstanceMove">The group is an instance move over two servers: its rules come from <see cref="GroupRuleEvaluator.EvaluateInstance"/>, which keeps the single-server table for a fresh Dst read.</param>
/// <param name="CacheIds">The cache ids the node registered for the group; null when the node does not say, which turns off the managed-set rule.</param>
internal sealed record GroupRegistration(string Name, bool Flagged, bool Listed, IReadOnlyList<string>? CacheIds = null, bool InstanceMove = false);
