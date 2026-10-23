namespace UiPath.Caching.Tests.Redis;

public class SlotFanOutTests
{
    [Fact]
    public async Task No_more_groups_than_the_limit_run_at_once_and_the_results_keep_group_order()
    {
        var groups = Enumerable.Range(0, 10).Select(i => new[] { i }).ToArray();
        var (running, peak) = (0, 0);
        var gate = new object();

        var results = await SlotFanOut.RunAsync(
            groups,
            0,
            async (_, group) =>
            {
                lock (gate)
                {
                    peak = Math.Max(peak, ++running);
                }

                await Task.Delay(20);
                lock (gate)
                {
                    running--;
                }

                return group[0];
            },
            -1,
            _ => { },
            maxInFlight: 3);

        results.Should().Equal(Enumerable.Range(0, 10));
        peak.Should().Be(3);
    }

    [Fact]
    public async Task A_group_that_throws_answers_the_failed_value_and_is_logged()
    {
        var logged = new List<Exception>();

        var results = await SlotFanOut.RunAsync(
            [[0], [1], [2]],
            0,
            (_, group) => group[0] == 1 ? throw new InvalidOperationException("slot") : ValueTask.FromResult(group[0]),
            -1,
            logged.Add,
            maxInFlight: 2);

        results.Should().Equal(0, -1, 2);
        logged.Should().ContainSingle().Which.Message.Should().Be("slot");
    }

    [Fact]
    public async Task A_cancelled_group_cancels_the_call_after_the_others_finish()
    {
        var other = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = SlotFanOut.RunAsync<int, int>(
            [[0], [1]],
            0,
            (_, group) => group[0] == 0 ? new ValueTask<int>(other.Task) : throw new OperationCanceledException(),
            -1,
            _ => throw new InvalidOperationException("a cancellation is not a slot failure"));

        run.IsCompleted.Should().BeFalse();
        other.SetResult(0);
        await run.Invoking(r => r).Should().ThrowAsync<OperationCanceledException>();
    }
}
