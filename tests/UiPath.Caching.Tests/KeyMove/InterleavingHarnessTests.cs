using UiPath.Caching.Tests.KeyMove.Harness;

namespace UiPath.Caching.Tests.KeyMove;

// Tries every ordering of up to three actors over one or two keys and checks the invariants at each step and in a final read.
// The search is exhaustive for each configuration (no truncation); two orderings that reach the same world are one state.
public class InterleavingHarnessTests
{
    public static TheoryData<string> Names => [.. Configurations.All.Keys];

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_ordering_keeps_the_invariants(string name) => HarnessRun.ShouldHold(Configurations.All[name](), Configurations.MayBlock.Contains(name));
}
