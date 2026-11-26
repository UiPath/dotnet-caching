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
    public async Task Names_round_trip_and_both_flag_sources_keep_the_existing_stage_semantics(string stored, string name, int flag, int currentFlag)
    {
        KeyMoveStates.NameOf(stored).Should().Be(name);
        KeyMoveStates.StoredOf(name).Should().Be(stored);
        KeyMoveStates.StoredOf(name.ToUpperInvariant()).Should().Be(stored);
        StepPlanner.StageFlagOf(State(stored)).Should().Be(currentFlag);

        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var named = new StringFlagStageSource((_, _, _) => ValueTask.FromResult<string?>(name));
        var uppercase = new StringFlagStageSource((_, _, _) => ValueTask.FromResult<string?>(name.ToUpperInvariant()));
        var number = new StringFlagStageSource((_, _, _) => ValueTask.FromResult<string?>(flag.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var integer = new IntFlagStageSource((_, _, _) => ValueTask.FromResult(flag));

        await AssertViewAsync(named, flag, time);
        await AssertViewAsync(uppercase, flag, time);
        await AssertViewAsync(number, flag, time);
        await AssertViewAsync(integer, flag, time);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
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
    [InlineData(KeyMoveStates.Barrier, "barrier")]
    [InlineData(KeyMoveStates.Ready, "ready")]
    public async Task An_unsupported_direction_reports_the_readable_stage(string stored, string name)
    {
        var plan = await StepPlanner.DecideAsync(Input(State(stored, "invalid")), Defaults);

        plan.AlertList.Should().Contain(new StageAlert("unknown_state", name + ":invalid"));
    }

    [Theory]
    [InlineData(KeyMoveStates.NotStarted, "not-started")]
    [InlineData(KeyMoveStates.Ready, "ready")]
    [InlineData(KeyMoveStates.Barrier, "barrier")]
    [InlineData(KeyMoveStates.Moving, "moving")]
    [InlineData(KeyMoveStates.Drained, "drained")]
    [InlineData(KeyMoveStates.Closed, "closed")]
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
