namespace UiPath.Caching.Migration;

/// <param name="RefusedOnSource">The source has none, so it throws <see cref="NotSupportedException"/> while it serves the group.</param>
internal sealed record CacheMemberRule(RuleClass Class, bool RefusedOnSource, string Note);
