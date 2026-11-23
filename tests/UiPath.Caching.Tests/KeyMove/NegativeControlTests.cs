using UiPath.Caching.Tests.KeyMove.Harness;

namespace UiPath.Caching.Tests.KeyMove;

// Each world passes with the specified protocol. With the weakness enabled, the harness reports the invariant it violates.
public class NegativeControlTests
{
    public static TheoryData<string> Names => [.. NegativeControls.All.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public void The_weakened_model_fails_the_harness_with_the_expected_invariant(string name)
    {
        var control = NegativeControls.All.Single(c => c.Name == name);

        var specified = HarnessRun.Explore(control.Build(ModelFaults.None));
        specified.Clean.Should().BeTrue("the protocol without the weakness holds: " + specified.Describe());

        var weakened = HarnessRun.Explore(control.Build(control.Faults));
        weakened.Violation.Should().NotBeNull($"the weakness '{name}' must break an invariant ({weakened.Describe()})");
        weakened.Violation!.Kind.Should().Be(control.Kind, weakened.Describe());
        weakened.Schedule.Should().NotBeEmpty();
    }
}
