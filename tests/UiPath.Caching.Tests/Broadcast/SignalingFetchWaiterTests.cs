using FluentAssertions.Extensions;

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
        // 30s interval so a completion within the generous 10s budget can only be the signal, never the
        // timeout. Awaiting the task directly (rather than racing it against a 500ms Task.Delay and
        // asserting which finished first) avoids a scheduling race that flaked under CI thread-pool load.
        using var sut = new SignalingFetchWaiter(30.Seconds());

        var task = sut.WaitAsync(CancellationToken.None);
        sut.Signal();

        await task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Signal_before_wait_is_consumed_by_next_wait()
    {
        using var sut = new SignalingFetchWaiter(5.Seconds());
        sut.Signal();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.WaitAsync(CancellationToken.None);

        sw.Elapsed.Should().BeLessThan(500.Milliseconds());
    }

    [Fact]
    public async Task Multiple_signals_between_waits_collapse_to_one()
    {
        var interval = 500.Milliseconds();
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
}

public class TimedFetchWaiterTests
{
    [Fact]
    public async Task WaitAsync_completes_after_timeout()
    {
        using var sut = new TimedFetchWaiter(50.Milliseconds());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.WaitAsync(TestContext.Current.CancellationToken);

        sw.Elapsed.Should().BeGreaterThanOrEqualTo(45.Milliseconds());
    }

    [Fact]
    public void Dispose_does_not_throw()
    {
        var sut = new TimedFetchWaiter(50.Milliseconds());

        Action act = () => sut.Dispose();

        act.Should().NotThrow();
    }
}
