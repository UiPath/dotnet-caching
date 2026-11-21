namespace UiPath.Caching.Migration;

/// <param name="Satisfied">The groups every node of this application has acknowledged.</param>
/// <param name="Blocker">Why no group is satisfied, when the node list itself is not good enough.</param>
internal readonly record struct PinVerdict(IReadOnlySet<string> Satisfied, string? Blocker);
