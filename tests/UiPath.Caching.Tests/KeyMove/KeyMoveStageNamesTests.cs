using UiPath.Caching.KeyMove;
using static UiPath.Caching.Tests.KeyMove.PlannerKit;

namespace UiPath.Caching.Tests.KeyMove;

public class KeyMoveStageNamesTests
{
    [Theory]
    [InlineData(KeyMoveStates.Off, "off", 0, 2)]
    [InlineData(KeyMoveStates.Preparing, "preparing", 1, 3)]
    [InlineData(KeyMoveStates.Switching, "switching", 2, 3)]
    [InlineData(KeyMoveStates.Copying, "copying", 3, 3)]
    [InlineData(KeyMoveStates.Copied, "copied", 4, 4)]
    [InlineData(KeyMoveStates.Done, "done", 5, 5)]
    public async Task Names_round_trip_and_numeric_flags_keep_the_existing_stage_semantics(string stored, string name, int flag, int currentFlag)
    {
        KeyMoveStates.NameOf(stored).Should().Be(name);
        KeyMoveStates.StoredOf(name).Should().Be(stored);
        KeyMoveStates.StoredOf(name.ToUpperInvariant()).Should().Be(stored);
        StepPlanner.StageFlagOf(State(stored)).Should().Be(currentFlag);

        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var number = new StringFlagStageSource((_, _, _) => ValueTask.FromResult<string?>(flag.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var integer = new IntFlagStageSource((_, _, _) => ValueTask.FromResult(flag));

        await AssertViewAsync(number, flag, time);
        await AssertViewAsync(integer, flag, time);
    }

    [Theory]
    [InlineData("off", 0)]
    [InlineData("copying", 3)]
    [InlineData("copied", 4)]
    [InlineData("done", 5)]
    [InlineData(" copying ", 3)]
    [InlineData("\t1 ", 1)]
    [InlineData(" 2\t", 2)]
    [InlineData("copied\n", 4)]
    [InlineData("3\r\n", 3)]
    public async Task Flag_names_and_numbers_ignore_case_and_surrounding_whitespace(string value, int expected)
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new StringFlagStageSource((_, _, _) => ValueTask.FromResult<string?>(value));
        var uppercase = new StringFlagStageSource((_, _, _) => ValueTask.FromResult<string?>($" \t{value.ToUpperInvariant()}\r\n"));

        await AssertViewAsync(source, expected, time);
        await AssertViewAsync(uppercase, expected, time);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public async Task Compatible_flags_start_rolling_a_copying_group_back(string value)
    {
        var source = new StringFlagStageSource((_, _, _) => ValueTask.FromResult<string?>(value));
        var flag = await source.GetStageAsync(Group, TestContext.Current.CancellationToken);
        var state = State(KeyMoveStates.Copying, nPop: 1);

        var plan = await StepPlanner.DecideAsync(Input(state, flag: Verdict(min: flag, lowered: flag)), Defaults);

        plan.Kind.Should().Be(StepKind.Transit);
        plan.Spec!.State.Should().Be(KeyMoveStates.Preparing);
        plan.Spec.Direction.Should().Be("rev");
    }

    [Theory]
    [InlineData("S9")]
    [InlineData("future-stage")]
    [InlineData("")]
    [InlineData(" ")]
    public void An_unrecognized_stored_stage_keeps_its_raw_value(string stored)
    {
        KeyMoveStates.NameOf(stored).Should().Be(stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n ")]
    [InlineData("preparing")]
    [InlineData("switching")]
    [InlineData(" PREPARING ")]
    [InlineData("SWITCHING\n")]
    [InlineData("not-started")]
    [InlineData("ready")]
    [InlineData("barrier")]
    [InlineData("moving")]
    [InlineData("drained")]
    [InlineData("closed")]
    [InlineData("returning")]
    [InlineData("copying-back")]
    [InlineData("copied-back")]
    [InlineData("unknown")]
    [InlineData("-1")]
    [InlineData("6")]
    [InlineData(KeyMoveStates.Off)]
    [InlineData(KeyMoveStates.Preparing)]
    [InlineData(KeyMoveStates.Switching)]
    [InlineData(KeyMoveStates.Copying)]
    [InlineData(KeyMoveStates.Copied)]
    [InlineData(KeyMoveStates.Done)]
    public async Task An_unreadable_flag_has_no_initial_view_and_keeps_the_last_valid_view(string? value)
    {
        var current = value;
        var source = new StringFlagStageSource((_, _, _) => ValueTask.FromResult(current));
        var poller = new StageFlagPoller(source, _ => true, new(), new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        (await source.GetStageAsync(Group, TestContext.Current.CancellationToken)).Should().BeNull();
        (await poller.StepAsync(Group, TestContext.Current.CancellationToken)).Should().BeFalse();
        poller.ViewOf(Group).Should().BeNull();

        current = "copied";
        (await poller.StepAsync(Group, TestContext.Current.CancellationToken)).Should().BeTrue();
        current = value;
        (await poller.StepAsync(Group, TestContext.Current.CancellationToken)).Should().BeFalse();
        poller.ViewOf(Group).Should().Be(4);

        current = $" \t{value?.ToUpperInvariant()}\r\n";
        (await source.GetStageAsync(Group, TestContext.Current.CancellationToken)).Should().BeNull();
        (await poller.StepAsync(Group, TestContext.Current.CancellationToken)).Should().BeFalse();
        poller.ViewOf(Group).Should().Be(4);
    }

    [Fact]
    public async Task The_string_getter_receives_the_group_an_invalid_default_and_the_cancellation_token()
    {
        var token = TestContext.Current.CancellationToken;
        var source = new StringFlagStageSource((group, fallback, cancellationToken) =>
        {
            group.Should().Be(Group);
            fallback.Should().BeEmpty();
            cancellationToken.Should().Be(token);
            return ValueTask.FromResult<string?>(fallback);
        });

        (await source.GetStageAsync(Group, token)).Should().BeNull();
    }

    [Theory]
    [InlineData(KeyMoveStates.Switching, "switching", "invalid")]
    [InlineData(KeyMoveStates.Preparing, "preparing", "invalid")]
    [InlineData("S9", "S9", "fwd")]
    [InlineData("S9", "S9 (reverse)", "rev")]
    public async Task An_unsupported_state_or_direction_reports_the_stage(string stored, string name, string direction)
    {
        var plan = await StepPlanner.DecideAsync(Input(State(stored, direction)), Defaults);

        plan.AlertList.Should().Contain(new StageAlert("unknown_state", name + ":" + direction));
    }

    [Theory]
    [InlineData(KeyMoveStates.Off, "off")]
    [InlineData(KeyMoveStates.Preparing, "returning")]
    [InlineData(KeyMoveStates.Switching, "switching (reverse)")]
    [InlineData(KeyMoveStates.Copying, "copying-back")]
    [InlineData(KeyMoveStates.Copied, "copied-back")]
    [InlineData(KeyMoveStates.Done, "done (reverse)")]
    [InlineData("S9", "S9 (reverse)")]
    public async Task A_frozen_managed_set_exception_names_the_stage(string stored, string name)
    {
        var upkeep = new ControlUpkeep(null!, null!, null!, new(), new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var act = () => upkeep.RebuildAsync(Group, State(stored, "rev"), "changed", null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"The managed set is frozen outside preparing and switching: the group is in {name} rev.");
    }

    [Fact]
    public void A_malformed_previous_state_is_not_echoed_in_the_exception()
    {
        var act = () => State(KeyMoveStates.Copying, prev: KeyMoveStates.Preparing).ToControlRecord([]);

        act.Should().Throw<FormatException>()
            .WithMessage("The state hash holds no valid previous state to copy into the control records.");
    }

    private static async Task AssertViewAsync(IMigrationStageSource source, int expected, TimeProvider time)
    {
        var poller = new StageFlagPoller(source, _ => true, new(), time);
        (await poller.StepAsync(Group, TestContext.Current.CancellationToken)).Should().BeTrue();
        poller.ViewOf(Group).Should().Be(expected);
    }
}
