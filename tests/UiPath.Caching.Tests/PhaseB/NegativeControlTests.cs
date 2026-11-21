using UiPath.Caching.Tests.PhaseB.Harness;

namespace UiPath.Caching.Tests.PhaseB;

// T-B2: with the specified protocol each control's world holds, and with the weakness the harness fails on the invariant it protects.
public class NegativeControlTests
{
    public static IEnumerable<object[]> Names => NegativeControls.All.Select(c => new object[] { c.Name });

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
