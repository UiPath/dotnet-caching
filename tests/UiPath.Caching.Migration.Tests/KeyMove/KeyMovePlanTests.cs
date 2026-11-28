using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration.Tests.KeyMove;

public class KeyMovePlanTests
{
    [Theory]
    [InlineData(KeyMoveStates.NotStarted, "fwd", "not-started")]
    [InlineData(KeyMoveStates.Ready, "fwd", "ready")]
    [InlineData(KeyMoveStates.Barrier, "fwd", "barrier")]
    [InlineData(KeyMoveStates.Moving, "fwd", "moving")]
    [InlineData(KeyMoveStates.Drained, "fwd", "drained")]
    [InlineData(KeyMoveStates.Closed, "fwd", "closed")]
    [InlineData(KeyMoveStates.Moving, "rev", "moving (reverse)")]
    [InlineData("S9", "fwd", "S9")]
    [InlineData("S9", "rev", "S9 (reverse)")]
    [InlineData("", "fwd", "")]
    [InlineData("", "rev", " (reverse)")]
    public void Stage_text_keeps_the_stored_state_and_direction(string stored, string direction, string expected)
    {
        var rules = GroupRules.Unaffected with { Acting = new ActedState(stored, direction, 1, "id", 0, false) };
        var plan = new KeyMovePlan(KeyMoveMode.Union, rules);

        plan.StageName.Should().Be(expected);
    }

    [Theory]
    [InlineData("Plain")]
    [InlineData("Union")]
    [InlineData("Unavailable")]
    [InlineData("Wait")]
    public void A_plan_without_an_acted_state_reports_its_mode(string expected)
    {
        var plan = new KeyMovePlan(Enum.Parse<KeyMoveMode>(expected), GroupRules.Unaffected);

        plan.StageName.Should().Be(expected);
    }
}
