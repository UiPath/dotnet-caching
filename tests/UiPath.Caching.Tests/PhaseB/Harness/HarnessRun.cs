namespace UiPath.Caching.Tests.PhaseB.Harness;

internal static class HarnessRun
{
    // Bound on distinct states per configuration; a search that reaches it is reported as truncated and fails the test.
    public const long MaxStates = 1_500_000;

    public static ExploreResult Explore(World world) => new Explorer(MaxStates).Run(world);

    // mayBlock: the configuration evicts a control record, after which every script refuses until a Rebuild, which the harness does not model.
    public static void ShouldHold(World world, bool mayBlock = false)
    {
        var result = Explore(world);
        result.Clean.Should().BeTrue(result.Describe());
        result.Terminals.Should().BeGreaterThan(0, "a search that never ends proves nothing");
        if (!mayBlock)
        {
            result.BlockedTerminals.Should().Be(0, "an actor that waits for a state that never comes is not a finished one");
        }
    }
}
