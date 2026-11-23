namespace UiPath.Caching.Migration.Tests.KeyMove.Harness;

/// <param name="Choices">The option picked at each decision, so a failing schedule can be replayed.</param>
internal sealed record InterleavingRun(IReadOnlyList<(int Chosen, int Options)> Trace, IReadOnlyList<string> Violations);
