
namespace UiPath.Caching.Tests.Broadcast;

public class SignalingFetchWaiterTests
{
    [Fact]
    public async Task WaitAsync_returns_when_timeout_elapses_without_signal()
    {
        using var sut = new SignalingFetchWaiter(50.Milliseconds());
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await sut.WaitAsync(CancellationToken.None);

        sw.Elapsed.Should().BeGreaterThanOrEqualTo(45.Milliseconds());
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
        using var sut = new SignalingFetchWaiter(interval);
        sut.Signal();
        sut.Signal();
        sut.Signal();

        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        await sut.WaitAsync(CancellationToken.None);
        sw1.Stop();
        sw1.Elapsed.Should().BeLessThan(interval - 200.Milliseconds(),
            "the first wait should be unblocked by a pending signal, not the timeout");

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        await sut.WaitAsync(CancellationToken.None);
        sw2.Stop();
        sw2.Elapsed.Should().BeGreaterThanOrEqualTo(interval - 50.Milliseconds(),
            "extra signals before the first wait should have collapsed; the second wait must hit the timeout");
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
        using var sut = new SignalingFetchWaiter(300.Milliseconds(), 30.Seconds());
        sut.SetNotifyLive(true);
        var wait = sut.WaitAsync(CancellationToken.None);
        await Task.Delay(50.Milliseconds(), TestContext.Current.CancellationToken);
        wait.IsCompleted.Should().BeFalse();

        sut.SetNotifyLive(false);

        await wait.WaitAsync(5.Seconds(), TestContext.Current.CancellationToken);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.WaitAsync(CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(250.Milliseconds(), "only the one wake was owed");
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
}
