namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <param name="Random">Seeded random schedules instead of every interleaving, for a scenario too large to enumerate.</param>
internal sealed record InterleavingScenario(string Name, string State, bool NoOrigin, string? InitialL, string? InitialN, Func<InterleavingContext, Func<Task>[]> Actors, int MaxRuns = 400, bool Random = false);
