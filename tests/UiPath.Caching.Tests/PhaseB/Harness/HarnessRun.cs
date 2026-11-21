namespace UiPath.Caching.Tests.PhaseB.Harness;

internal static class HarnessRun
{
    // Bound on distinct states per configuration; a search that reaches it is reported as truncated and fails the test.
    public const long MaxStates = 1_500_000;

    public static ExploreResult Explore(World world) => new Explorer(MaxStates).Run(world);

    public static void ShouldHold(World world)
    {
        var result = Explore(world);
        result.Clean.Should().BeTrue(result.Describe());
        result.Terminals.Should().BeGreaterThan(0, "a search that never ends proves nothing");
    }
}
