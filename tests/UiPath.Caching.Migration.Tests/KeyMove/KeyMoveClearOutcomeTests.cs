using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// How the outcome of the gate protocol becomes a return or a CacheUnavailableException, and which hooks the Clear hands the protocol.
public sealed class KeyMoveClearOutcomeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_cleared_outcome_returns_and_the_hooks_run_the_two_events_around_the_source_clear_and_the_sweep_of_N()
    {
        var order = new List<string>();
        var clear = Build(
            async (hooks, token) =>
            {
                await hooks.PublishFirst(token);
                await hooks.Sweep(token);
                await hooks.PublishSecond(token);
                return ClearOutcome.Cleared;
            },
            order);

        await clear.ClearAsync(Ct);

        order.Should().Equal("local", "publish", "source", "sweepN:g/c", "local", "publish");
    }

    [Fact]
    public async Task An_unavailable_outcome_is_tried_again_and_a_later_cleared_returns()
    {
        var calls = 0;
        var clear = Build((_, _) => Task.FromResult(++calls < 3 ? ClearOutcome.Unavailable : ClearOutcome.Cleared), []);

        await clear.ClearAsync(Ct);

        calls.Should().Be(3);
    }

    [Fact]
    public async Task An_unavailable_outcome_on_every_attempt_throws_after_exactly_the_attempts_the_options_allow()
    {
        var calls = 0;
        var clear = Build((_, _) => Task.FromResult(Count(ref calls, ClearOutcome.Unavailable)), [], attempts: 4);

        var act = () => clear.ClearAsync(Ct);

        await act.Should().ThrowAsync<CacheUnavailableException>().WithMessage("*unavailable*");
        calls.Should().Be(4);
    }

    [Fact]
    public async Task A_failed_outcome_throws_at_once_with_no_retry()
    {
        var calls = 0;
        var clear = Build((_, _) => Task.FromResult(Count(ref calls, ClearOutcome.Failed)), []);

        var act = () => clear.ClearAsync(Ct);

        await act.Should().ThrowAsync<CacheUnavailableException>().WithMessage("*could not be made safe*");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task The_wait_between_attempts_doubles_and_a_cancelled_call_stops_waiting()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var calls = 0;
        using var stop = new CancellationTokenSource();
        var clear = new KeyMoveClear("g", "c", (_, _, _, _) => Task.FromResult(Count(ref calls, ClearOutcome.Unavailable)), (_, _, _, _) => Task.FromResult(0L), _ => ValueTask.CompletedTask, () => { }, () => ValueTask.CompletedTask, 5, time, TimeSpan.FromSeconds(1));

        var running = clear.ClearAsync(stop.Token);
        await ArmedAsync(time);
        calls.Should().Be(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => Volatile.Read(ref calls) == 2);
        await ArmedAsync(time);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, Ct);
        calls.Should().Be(2, "the second wait is two seconds");
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => Volatile.Read(ref calls) == 3);
        await ArmedAsync(time);
        await stop.CancelAsync();

        var waiting = async () => await running;
        await waiting.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ClearOutcome Count(ref int calls, ClearOutcome outcome)
    {
        calls++;
        return outcome;
    }

    private static async Task ArmedAsync(ManualTimeProvider time)
    {
        await WaitAsync(() => time.HasTimers);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var tries = 0;
        while (!condition())
        {
            tries.Should().BeLessThan(2000, "the condition never held");
            await Task.Delay(5, Ct);
            tries++;
        }
    }

    private static KeyMoveClear Build(Func<ClearHooks, CancellationToken, Task<ClearOutcome>> gated, List<string> order, int attempts = 3) =>
        new(
            "g",
            "c",
            (_, _, hooks, token) => gated(hooks, token),
            (group, cache, _, _) =>
            {
                order.Add($"sweepN:{group}/{cache}");
                return Task.FromResult(0L);
            },
            _ =>
            {
                order.Add("source");
                return ValueTask.CompletedTask;
            },
            () => order.Add("local"),
            () =>
            {
                order.Add("publish");
                return ValueTask.CompletedTask;
            },
            attempts,
            TimeProvider.System,
            TimeSpan.Zero);
}
