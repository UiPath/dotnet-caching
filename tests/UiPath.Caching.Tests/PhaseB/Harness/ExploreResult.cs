namespace UiPath.Caching.Tests.PhaseB.Harness;

/// <param name="States">Distinct world states visited.</param>
/// <param name="Schedules">The number of distinct complete orderings the visited states stand for (each state is expanded once, its paths are counted).</param>
/// <param name="Terminals">Distinct end states, each checked with a final read.</param>
/// <param name="BlockedTerminals">End states where an actor had not finished and could not move (it waits for a state that never comes).</param>
/// <param name="Truncated">True when the state bound stopped the search before it was exhaustive.</param>
internal sealed record ExploreResult(long States, double Schedules, long Terminals, long BlockedTerminals, bool Truncated, Violation? Violation, IReadOnlyList<string> Schedule)
{
    public bool Clean => Violation is null && !Truncated;

    public string Describe() => Violation is null
        ? $"{States} states, {Schedules:0} schedules{(Truncated ? ", TRUNCATED" : string.Empty)}"
        : $"{(Violation.Final ? "final state" : "invariant")} {Violation.Kind} broken: {Violation.Message}\nschedule:\n  " + string.Join("\n  ", Schedule.Select((s, i) => $"{i + 1}. {s}"));
}
