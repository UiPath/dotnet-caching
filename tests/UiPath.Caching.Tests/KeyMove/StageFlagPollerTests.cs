using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

// The stage source: the sentinel -1, values outside 0 to 5, a failed or hung read, and the poll intervals.
public class StageFlagPollerTests
{
    private const string Group = "g1";

    private readonly ManualTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly KeyMoveStateOptions _options = new();

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(6, null)]
    [InlineData(-7, null)]
    [InlineData(int.MaxValue, null)]
    public async Task The_int_flag_helper_maps_the_sentinel_and_every_value_outside_0_to_5_to_null(int value, int? expected)
    {
        var source = new IntFlagStageSource((_, _, _) => ValueTask.FromResult(value));

        (await source.GetStageAsync(Group, CancellationToken.None)).Should().Be(expected);
    }

    [Fact]
    public async Task The_helper_hands_the_getter_the_group_and_the_sentinel_as_its_default_so_a_failed_evaluation_is_null()
    {
        string? asked = null;
        int? offered = null;
        var source = new IntFlagStageSource((group, defaultValue, _) =>
        {
            asked = group;
            offered = defaultValue;
            return ValueTask.FromResult(defaultValue);
        });

        (await source.GetStageAsync("orders", CancellationToken.None)).Should().BeNull("a flag system that returns its default on a failed read is a read that failed");

        asked.Should().Be("orders");
        offered.Should().Be(-1);
    }

    [Fact]
    public async Task The_source_is_given_a_token_that_the_poll_interval_cancels_so_a_call_that_honours_it_stops()
    {
        var seen = new TaskCompletionSource<CancellationToken>();
        var poller = Poller(new Source(() => null)
        {
            GateWithToken = token =>
            {
                seen.TrySetResult(token);
                return new TaskCompletionSource<int?>().Task;
            },
        });
        var step = poller.StepAsync(Group, CancellationToken.None);
        var token = await seen.Task;
        token.IsCancellationRequested.Should().BeFalse();

        await TimeSteps.ArmedAsync(_time);
        _time.Advance(TimeSpan.FromSeconds(2));

        (await step).Should().BeFalse();
        token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task A_pod_has_no_view_before_its_first_value_and_a_null_read_keeps_the_last_view()
    {
        var value = (int?)null;
        var poller = Poller(new Source(() => value));

        poller.ViewOf(Group).Should().BeNull();
        (await poller.StepAsync(Group, CancellationToken.None)).Should().BeFalse();
        poller.ViewOf(Group).Should().BeNull("a failed first read is not a stage 0");

        value = 4;
        (await poller.StepAsync(Group, CancellationToken.None)).Should().BeTrue();
        value = null;
        await poller.StepAsync(Group, CancellationToken.None);
        poller.ViewOf(Group).Should().Be(4);
    }

    [Fact]
    public async Task A_throwing_source_or_a_value_outside_0_to_5_keeps_the_last_view()
    {
        var calls = 0;
        var poller = Poller(new Source(() => ++calls switch
        {
            1 => 2,
            2 => throw new InvalidOperationException("flag system down"),
            _ => 9,
        }));

        await poller.StepAsync(Group, CancellationToken.None);
        await poller.StepAsync(Group, CancellationToken.None);
        await poller.StepAsync(Group, CancellationToken.None);

        poller.ViewOf(Group).Should().Be(2);
    }

    [Fact]
    public async Task A_hung_call_is_bounded_by_one_poll_interval_and_cannot_stop_the_loop()
    {
        var hung = new TaskCompletionSource<int?>();
        var calls = 0;
        var poller = Poller(new Source(() => null) { Gate = () => ++calls == 1 ? hung.Task : Task.FromResult<int?>(5) });
        var first = poller.StepAsync(Group, CancellationToken.None);

        await TimeSteps.ArmedAsync(_time);
        _time.Advance(TimeSpan.FromSeconds(2));
        (await first).Should().BeFalse();
        poller.ViewOf(Group).Should().BeNull();

        (await poller.StepAsync(Group, CancellationToken.None)).Should().BeTrue();
        poller.ViewOf(Group).Should().Be(5);
    }

    [Fact]
    public async Task A_group_in_S0_to_S4_is_polled_every_2_seconds_and_any_other_every_60()
    {
        var reads = 0;
        var active = true;
        var poller = new StageFlagPoller(new Source(() => ++reads), _ => active, _options, _time);
        poller.IntervalFor(Group).Should().Be(TimeSpan.FromSeconds(2));
        poller.Start([Group], CancellationToken.None);
        try
        {
            await TimeSteps.UntilAsync(() => reads == 1);
            await TimeSteps.AdvanceArmedAsync(_time, TimeSpan.FromSeconds(2));
            await TimeSteps.UntilAsync(() => reads == 2);

            active = false;
            poller.IntervalFor(Group).Should().Be(TimeSpan.FromSeconds(60));
            await TimeSteps.AdvanceArmedAsync(_time, TimeSpan.FromSeconds(2));
            await TimeSteps.UntilAsync(() => reads == 3);
            await TimeSteps.ArmedAsync(_time);
            _time.Advance(TimeSpan.FromSeconds(59));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            reads.Should().Be(3, "the slow interval has not elapsed");
            _time.Advance(TimeSpan.FromSeconds(1));
            await TimeSteps.UntilAsync(() => reads == 4);
        }
        finally
        {
            await poller.StopAsync();
        }
    }

    private StageFlagPoller Poller(IMigrationStageSource source, bool active = true) => new(source, _ => active, _options, _time);

    private sealed class Source(Func<int?> read) : IMigrationStageSource
    {
        public Func<Task<int?>>? Gate { get; init; }

        public Func<CancellationToken, Task<int?>>? GateWithToken { get; init; }

        public ValueTask<int?> GetStageAsync(string group, CancellationToken cancellationToken) =>
            GateWithToken is { } withToken ? new ValueTask<int?>(withToken(cancellationToken)) : Gate is { } gate ? new ValueTask<int?>(gate()) : ValueTask.FromResult(read());
    }
}
