namespace UiPath.Caching.Migration;

/// <param name="Satisfied">The groups every node of this application has acknowledged.</param>
/// <param name="Blocker">Why no group is satisfied, when the node list itself is not good enough.</param>
/// <param name="Required">The node-list fields of this application's routers, whose acknowledgements a switch has to be recorded against.</param>
internal readonly record struct PinVerdict(IReadOnlySet<string> Satisfied, string? Blocker, IReadOnlyList<string> Required);
