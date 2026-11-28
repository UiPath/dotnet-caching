using UiPath.Caching.KeyMove;
using static UiPath.Caching.Tests.KeyMove.PlannerKit;

namespace UiPath.Caching.Tests.KeyMove;

public class KeyMoveStageNamesTests
{
    [Theory]
    [InlineData(KeyMoveStates.NotStarted, "not-started", 0, 2)]
    [InlineData(KeyMoveStates.Ready, "ready", 1, 3)]
    [InlineData(KeyMoveStates.Barrier, "barrier", 2, 3)]
    [InlineData(KeyMoveStates.Moving, "moving", 3, 3)]
    [InlineData(KeyMoveStates.Drained, "drained", 4, 4)]
    [InlineData(KeyMoveStates.Closed, "closed", 5, 5)]
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
    [InlineData("not-started", 0)]
    [InlineData("moving", 3)]
    [InlineData("drained", 4)]
    [InlineData("closed", 5)]
    [InlineData(" moving ", 3)]
    [InlineData("\t1 ", 1)]
    [InlineData(" 2\t", 2)]
    [InlineData("drained\n", 4)]
    [InlineData("3\r\n", 3)]
    public async Task Flag_names_and_numbers_ignore_case_and_surrounding_whitespace(string value, int expected)
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new StringFlagStageSource((_, _, _) => ValueTask.FromResult<string?>(value));
        var uppercase = new StringFlagStageSource((_, _, _) => ValueTask.FromResult<string?>(value.ToUpperInvariant()));

        await AssertViewAsync(source, expected, time);
        await AssertViewAsync(uppercase, expected, time);
    }

    [Theory]
    [InlineData("not-started")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public async Task Compatible_flags_start_rolling_a_moving_group_back(string value)
    {
        var source = new StringFlagStageSource((_, _, _) => ValueTask.FromResult<string?>(value));
        var flag = await source.GetStageAsync(Group, TestContext.Current.CancellationToken);
        var state = State(KeyMoveStates.Moving, nPop: 1);

        var plan = await StepPlanner.DecideAsync(Input(state, flag: Verdict(min: flag, lowered: flag)), Defaults);

        plan.Kind.Should().Be(StepKind.Transit);
        plan.Spec!.State.Should().Be(KeyMoveStates.Ready);
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
    [InlineData("ready")]
    [InlineData("barrier")]
    [InlineData(" READY ")]
    [InlineData("BARRIER\n")]
    [InlineData("unknown")]
    [InlineData("-1")]
    [InlineData("6")]
    [InlineData(KeyMoveStates.NotStarted)]
    [InlineData(KeyMoveStates.Ready)]
    [InlineData(KeyMoveStates.Barrier)]
    [InlineData(KeyMoveStates.Moving)]
    [InlineData(KeyMoveStates.Drained)]
    [InlineData(KeyMoveStates.Closed)]
    public async Task An_unreadable_flag_has_no_initial_view_and_keeps_the_last_valid_view(string? value)
    {
        var current = value;
        var source = new StringFlagStageSource((_, _, _) => ValueTask.FromResult(current));
        var poller = new StageFlagPoller(source, _ => true, new(), new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        (await source.GetStageAsync(Group, TestContext.Current.CancellationToken)).Should().BeNull();
        (await poller.StepAsync(Group, TestContext.Current.CancellationToken)).Should().BeFalse();
        poller.ViewOf(Group).Should().BeNull();

        current = "drained";
        (await poller.StepAsync(Group, TestContext.Current.CancellationToken)).Should().BeTrue();
        current = value;
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
    [InlineData(KeyMoveStates.Barrier, "barrier", "invalid")]
    [InlineData(KeyMoveStates.Ready, "ready", "invalid")]
    [InlineData("S9", "S9", "fwd")]
    [InlineData("S9", "S9", "rev")]
    public async Task An_unsupported_state_or_direction_reports_the_stage(string stored, string name, string direction)
    {
        var plan = await StepPlanner.DecideAsync(Input(State(stored, direction)), Defaults);

        plan.AlertList.Should().Contain(new StageAlert("unknown_state", name + ":" + direction));
    }

    [Theory]
    [InlineData(KeyMoveStates.NotStarted, "not-started")]
    [InlineData(KeyMoveStates.Ready, "ready")]
    [InlineData(KeyMoveStates.Barrier, "barrier")]
    [InlineData(KeyMoveStates.Moving, "moving")]
    [InlineData(KeyMoveStates.Drained, "drained")]
    [InlineData(KeyMoveStates.Closed, "closed")]
    [InlineData("S9", "S9")]
    public async Task A_frozen_managed_set_exception_names_the_stage(string stored, string name)
    {
        var upkeep = new ControlUpkeep(null!, null!, null!, new(), new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var act = () => upkeep.RebuildAsync(Group, State(stored, "rev"), "changed", null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"The managed set is frozen outside ready and barrier: the group is in {name} rev.");
    }

    [Fact]
    public void A_malformed_previous_state_is_not_echoed_in_the_exception()
    {
        var act = () => State(KeyMoveStates.Moving, prev: KeyMoveStates.Ready).ToControlRecord([]);

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
