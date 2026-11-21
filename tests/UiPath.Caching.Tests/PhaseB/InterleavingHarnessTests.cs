using UiPath.Caching.Tests.PhaseB.Harness;

namespace UiPath.Caching.Tests.PhaseB;

// T-B1: every ordering of up to three actors over one or two keys, with the invariants of 1.2 and 4 checked at each step and by a final read.
// The search is exhaustive for each configuration (no truncation); two orderings that reach the same world are one state.
public class InterleavingHarnessTests
{
    public static IEnumerable<object[]> Names => Configurations.All.Keys.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_ordering_keeps_the_invariants(string name) => HarnessRun.ShouldHold(Configurations.All[name](), Configurations.MayBlock.Contains(name));
}
