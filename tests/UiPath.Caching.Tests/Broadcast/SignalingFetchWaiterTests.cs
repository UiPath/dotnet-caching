
namespace UiPath.Caching.Tests.Broadcast;

public class SignalingFetchWaiterTests
{
    [Fact]
    public async Task WaitAsync_returns_when_timeout_elapses_without_signal()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(50.Milliseconds(), timeProvider: clock);

        var wait = sut.WaitAsync(CancellationToken.None);
        await StillWaitingAsync(wait);
        clock.Advance(49.Milliseconds());
        await StillWaitingAsync(wait);
        clock.Advance(1.Milliseconds());

        await wait.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitAsync_returns_quickly_when_signaled()
    {
        using var sut = new SignalingFetchWaiter(30.Seconds());

        var task = sut.WaitAsync(CancellationToken.None);
        sut.Signal();

        await task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Signal_before_wait_is_consumed_by_next_wait()
    {
        using var sut = new SignalingFetchWaiter(30.Seconds());
        sut.Signal();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.WaitAsync(CancellationToken.None);

        sw.Elapsed.Should().BeLessThan(5.Seconds());
    }

    [Fact]
    public async Task Multiple_signals_between_waits_collapse_to_one()
    {
        var interval = 2.Seconds();
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(interval, timeProvider: clock);
        sut.Signal();
        sut.Signal();
        sut.Signal();

        await sut.WaitAsync(CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var second = sut.WaitAsync(CancellationToken.None);
        await StillWaitingAsync(second);
        clock.Advance(interval - 1.Milliseconds());
        await StillWaitingAsync(second);
        clock.Advance(1.Milliseconds());
        await second.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_signal_while_one_is_pending_throws_nothing()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(30.Seconds(), timeProvider: clock);
        var threadId = Environment.CurrentManagedThreadId;
        var thrown = 0;
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == threadId && e.Exception is SemaphoreFullException)
            {
                Interlocked.Increment(ref thrown);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            sut.Signal();
            sut.Signal();
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        thrown.Should().Be(0);
        await sut.WaitAsync(CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        var next = sut.WaitAsync(200.Milliseconds(), CancellationToken.None);
        await StillWaitingAsync(next);
        clock.Advance(200.Milliseconds());
        await next.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_zero_timeout_wait_consumes_a_pending_signal_on_the_clock_path()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(30.Seconds(), timeProvider: clock);
        sut.Signal();

        await sut.WaitAsync(TimeSpan.Zero, CancellationToken.None);

        var next = sut.WaitAsync(100.Milliseconds(), CancellationToken.None);
        await StillWaitingAsync(next);
        clock.Advance(100.Milliseconds());
        await next.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_cancelled_wait_on_the_clock_path_throws_with_the_callers_token()
    {
        using var sut = new SignalingFetchWaiter(30.Seconds(), timeProvider: NewClock());
        using var cts = new CancellationTokenSource();
        var task = sut.WaitAsync(cts.Token);
        cts.Cancel();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        thrown.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task WaitAsync_throws_when_token_cancelled()
    {
        using var sut = new SignalingFetchWaiter(5.Seconds());
        using var cts = new CancellationTokenSource();
        var task = sut.WaitAsync(cts.Token);
        cts.Cancel();

        Func<Task> act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Signal_after_dispose_is_a_noop()
    {
        var sut = new SignalingFetchWaiter(50.Milliseconds());
        sut.Dispose();

        Action act = () => sut.Signal();
        act.Should().NotThrow();

        await Task.CompletedTask;
    }

    [Fact]
    public async Task WaitAsync_after_dispose_does_not_throw()
    {
        var sut = new SignalingFetchWaiter(50.Milliseconds());
        sut.Dispose();

        Func<Task> act = async () => await sut.WaitAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var sut = new SignalingFetchWaiter(50.Milliseconds());
        sut.Dispose();

        Action act = () => sut.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task A_wait_lasts_the_poll_interval_until_the_notify_is_live()
    {
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 30.Seconds());

        Func<Task> act = () => sut.WaitAsync(CancellationToken.None);

        await act.Should().CompleteWithinAsync(10.Seconds());
    }

    [Fact]
    public async Task A_wait_lasts_the_notified_interval_while_the_notify_is_live()
    {
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 30.Seconds());
        sut.SetNotifyLive(true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var wait = sut.WaitAsync(cts.Token);

        (await Task.WhenAny(wait, Task.Delay(600.Milliseconds(), TestContext.Current.CancellationToken))).Should().NotBeSameAs(wait, "the short interval is 100 ms");
        await cts.CancelAsync();
        Func<Task> act = () => wait;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Losing_the_notify_ends_the_wait_in_progress_and_the_next_one_is_short()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(300.Milliseconds(), 30.Seconds(), clock);
        sut.SetNotifyLive(true);
        var wait = sut.WaitAsync(CancellationToken.None);
        clock.Advance(50.Milliseconds());
        await StillWaitingAsync(wait);

        sut.SetNotifyLive(false);

        await wait.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);
        var next = sut.WaitAsync(CancellationToken.None);
        await StillWaitingAsync(next);
        clock.Advance(299.Milliseconds());
        await StillWaitingAsync(next);
        clock.Advance(1.Milliseconds());
        await next.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Gaining_the_notify_does_not_wake_the_wait_in_progress()
    {
        using var sut = new SignalingFetchWaiter(30.Seconds(), 30.Seconds());
        var wait = sut.WaitAsync(CancellationToken.None);

        sut.SetNotifyLive(true);

        (await Task.WhenAny(wait, Task.Delay(300.Milliseconds(), TestContext.Current.CancellationToken))).Should().NotBeSameAs(wait);
    }

    [Fact]
    public async Task Without_a_notified_interval_a_live_notify_changes_nothing()
    {
        using var sut = new SignalingFetchWaiter(100.Milliseconds());
        sut.SetNotifyLive(true);

        Func<Task> act = () => sut.WaitAsync(CancellationToken.None);

        await act.Should().CompleteWithinAsync(10.Seconds());
    }

    [Fact]
    public async Task An_explicit_timeout_ignores_the_notify_state()
    {
        using var sut = new SignalingFetchWaiter(30.Seconds(), 30.Seconds());
        sut.SetNotifyLive(true);

        Func<Task> act = () => sut.WaitAsync(100.Milliseconds(), CancellationToken.None);

        await act.Should().CompleteWithinAsync(10.Seconds());
    }

    private static ManualTimeProvider NewClock() => new(DateTimeOffset.UnixEpoch);

    /// <summary>Gives a wait that should still be pending a real moment to finish wrongly, then asserts it has not.</summary>
    private static async Task StillWaitingAsync(Task wait)
    {
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        wait.IsCompleted.Should().BeFalse();
    }
}
