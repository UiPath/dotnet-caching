namespace UiPath.Caching.Migration.Tests.KeyMove;

// The retry loop survives a throwing application callback, and a full set gives up immediately.
public sealed class MoveRetrySetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_callback_that_throws_on_exhaustion_does_not_end_the_retries()
    {
        var options = new KeyMoveOptions { RetryInitial = TimeSpan.FromMilliseconds(10), RetryMax = TimeSpan.FromMilliseconds(10) };
        var told = 0;
        var toldOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ObservedClock(DateTimeOffset.UnixEpoch);
        using var set = new MoveRetrySet(clock, options, _ =>
        {
            Interlocked.Increment(ref told);
            toldOnce.TrySetResult();
            throw new InvalidOperationException("the alert delegate failed");
        });
        set.Add("old", clock.ToDateTimeOffset(TimeSpan.FromMilliseconds(-1)), _ => Task.FromResult(false));
        set.Add("later", clock.ToDateTimeOffset(TimeSpan.FromMinutes(1)), _ => Task.FromResult(true));
        var settled = set.SettledAsync("later") ?? Task.CompletedTask;

        await clock.AdvanceNextTimerAsync(Ct);
        await toldOnce.Task.WaitAsync(Ct);
        await settled.WaitAsync(Ct);

        Volatile.Read(ref told).Should().Be(1);
        set.Count.Should().Be(0);

        // The loop is still there for a retry added after the failure.
        var again = new TaskCompletionSource();
        set.Add("after", clock.ToDateTimeOffset(TimeSpan.FromMinutes(1)), _ =>
        {
            again.TrySetResult();
            return Task.FromResult(true);
        });
        await clock.AdvanceNextTimerAsync(Ct);
        await again.Task.WaitAsync(Ct);
    }

    [Fact]
    public async Task Repeated_stop_keeps_the_token_alive_until_the_retry_and_cancellation_callback_return()
    {
        var running = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishCallback = new ManualResetEventSlim();
        var clock = new ObservedClock(DateTimeOffset.UnixEpoch);
        using var set = new MoveRetrySet(clock, new KeyMoveOptions { RetryInitial = TimeSpan.FromMilliseconds(10) }, _ => { });
        set.Add("a", clock.ToDateTimeOffset(TimeSpan.FromMinutes(1)), AttemptAsync);
        var settled = set.SettledAsync("a") ?? throw new InvalidOperationException("The retry was not registered.");
        await clock.AdvanceNextTimerAsync(Ct);
        var token = await running.Task.WaitAsync(Ct);
        var stop = set.StopAsync();

        try
        {
            set.StopAsync().Should().BeSameAs(stop);
            token.WaitHandle.WaitOne(0).Should().BeTrue();
            await cancelling.Task.WaitAsync(Ct);
            finishAttempt.SetResult();
            await settled.WaitAsync(Ct);
            stop.IsCompleted.Should().BeFalse();
            token.WaitHandle.WaitOne(0).Should().BeTrue();
        }
        finally
        {
            finishAttempt.TrySetResult();
            finishCallback.Set();
            await stop.WaitAsync(Ct);
        }

        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
        await set.StopAsync();

        async Task<bool> AttemptAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken.Register(() =>
            {
                cancelling.SetResult();
                finishCallback.Wait(Ct);
            });

            running.SetResult(cancellationToken);
            await finishAttempt.Task.WaitAsync(Ct);
            return true;
        }
    }

    [Fact]
    public async Task A_full_set_gives_up_at_once_and_a_settled_task_completes_when_a_key_is_done()
    {
        var gave = new List<string>();
        var clock = new ObservedClock(DateTimeOffset.UnixEpoch);
        using var set = new MoveRetrySet(clock, new KeyMoveOptions { RetryCapacity = 1 }, gave.Add, background: false);
        set.Add("a", clock.ToDateTimeOffset(TimeSpan.FromMinutes(1)), _ => Task.FromResult(true));
        set.Add("b", clock.ToDateTimeOffset(TimeSpan.FromMinutes(1)), _ => Task.FromResult(true));
        var settled = set.SettledAsync("a");

        gave.Should().Equal("b");
        settled!.IsCompleted.Should().BeFalse();
        await set.RunDueAsync(all: true);

        settled.IsCompleted.Should().BeTrue();
        set.SettledAsync("a").Should().BeNull();
        await Task.CompletedTask;
    }
}
