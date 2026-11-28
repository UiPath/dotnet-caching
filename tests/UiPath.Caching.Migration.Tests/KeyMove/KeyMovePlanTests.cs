using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration.Tests.KeyMove;

public class KeyMovePlanTests
{
    [Theory]
    [InlineData(KeyMoveStates.Off, "fwd", "off")]
    [InlineData(KeyMoveStates.Preparing, "fwd", "preparing")]
    [InlineData(KeyMoveStates.Switching, "fwd", "switching")]
    [InlineData(KeyMoveStates.Copying, "fwd", "copying")]
    [InlineData(KeyMoveStates.Copied, "fwd", "copied")]
    [InlineData(KeyMoveStates.Done, "fwd", "done")]
    [InlineData(KeyMoveStates.Off, "rev", "off")]
    [InlineData(KeyMoveStates.Off, "none", "off")]
    [InlineData(KeyMoveStates.Preparing, "rev", "returning")]
    [InlineData(KeyMoveStates.Switching, "rev", "switching (reverse)")]
    [InlineData(KeyMoveStates.Copying, "rev", "copying-back")]
    [InlineData(KeyMoveStates.Copied, "rev", "copied-back")]
    [InlineData(KeyMoveStates.Done, "rev", "done (reverse)")]
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
