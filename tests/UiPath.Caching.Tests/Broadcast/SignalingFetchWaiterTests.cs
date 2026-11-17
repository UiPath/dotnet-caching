
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
        // The clock never moves, so only the signal can end the wait.
        using var sut = new SignalingFetchWaiter(30.Seconds(), timeProvider: NewClock());

        var wait = sut.WaitAsync(CancellationToken.None);
        wait.IsCompleted.Should().BeFalse();
        sut.Signal();

        await wait.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        wait.IsCompletedSuccessfully.Should().BeTrue();
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
        await cts.CancelAsync();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        thrown.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task WaitAsync_throws_when_token_cancelled()
    {
        using var sut = new SignalingFetchWaiter(5.Seconds());
        using var cts = new CancellationTokenSource();
        var task = sut.WaitAsync(cts.Token);
        await cts.CancelAsync();

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

    [Fact]
    public async Task Two_live_waits_that_time_out_onto_other_nodes_entries_shorten_the_next_wait()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 5.Seconds(), clock);
        sut.SetNotifyLive(true);

        await TimeOutAsync(sut, clock, 5.Seconds());
        sut.ObserveRead(fromOthers: true).Should().Be(DoorbellHealth.Unchanged, "one late entry may be a doorbell that raced the poll");
        await TimeOutAsync(sut, clock, 5.Seconds());
        sut.ObserveRead(fromOthers: true).Should().Be(DoorbellHealth.Missed);

        sut.Shortened.Should().BeTrue();
        await TimeOutAsync(sut, clock, 100.Milliseconds());
    }

    [Fact]
    public async Task A_doorbell_that_delivers_between_two_misses_keeps_the_long_wait()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 5.Seconds(), clock);
        sut.SetNotifyLive(true);

        await TimeOutAsync(sut, clock, 5.Seconds());
        sut.ObserveRead(fromOthers: true);
        sut.Ring();
        await sut.WaitAsync(CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        sut.ObserveRead(fromOthers: true);
        await TimeOutAsync(sut, clock, 5.Seconds());

        sut.ObserveRead(fromOthers: true).Should().Be(DoorbellHealth.Unchanged, "the misses were not in a row");
        sut.Shortened.Should().BeFalse();
    }

    [Fact]
    public async Task A_doorbell_whose_read_finds_nothing_still_resets_the_count()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 5.Seconds(), clock);
        sut.SetNotifyLive(true);

        // The poll timed out a moment after another node's XADD, before its doorbell arrived.
        await TimeOutAsync(sut, clock, 5.Seconds());
        sut.ObserveRead(fromOthers: true);
        // The late doorbell wakes a read that finds the entry already taken.
        sut.Ring();
        await sut.WaitAsync(CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        sut.ObserveRead(fromOthers: false);
        await TimeOutAsync(sut, clock, 5.Seconds());

        sut.ObserveRead(fromOthers: true).Should().Be(DoorbellHealth.Unchanged, "a doorbell arrived between the two late entries");
        sut.Shortened.Should().BeFalse();
    }

    [Fact]
    public void Reads_are_observed_only_while_a_live_doorbell_lengthens_the_wait()
    {
        using var longer = new SignalingFetchWaiter(100.Milliseconds(), 5.Seconds());
        using var same = new SignalingFetchWaiter(100.Milliseconds(), 100.Milliseconds());
        using var none = new SignalingFetchWaiter(100.Milliseconds());

        longer.ObservesReads.Should().BeFalse("the doorbell is not live yet");
        longer.SetNotifyLive(true);
        same.SetNotifyLive(true);
        none.SetNotifyLive(true);

        longer.ObservesReads.Should().BeTrue();
        same.ObservesReads.Should().BeFalse("the wait is the poll interval anyway");
        none.ObservesReads.Should().BeFalse();
    }

    [Fact]
    public async Task A_doorbell_that_rang_as_the_wait_timed_out_is_not_a_miss()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 5.Seconds(), clock);
        sut.SetNotifyLive(true);

        for (var i = 0; i < 3; i++)
        {
            await TimeOutAsync(sut, clock, 5.Seconds());
            sut.Ring();
            sut.ObserveRead(fromOthers: true).Should().Be(DoorbellHealth.Unchanged);
            // The ring left a wake-up pending; the read it causes finds nothing.
            await sut.WaitAsync(CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
            sut.ObserveRead(fromOthers: false);
        }

        sut.Shortened.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_timed_out_wait_is_no_miss_without_a_live_doorbell_or_without_other_nodes_entries(bool live, bool fromOthers)
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 5.Seconds(), clock);
        sut.SetNotifyLive(live);

        for (var i = 0; i < 3; i++)
        {
            await TimeOutAsync(sut, clock, live ? 5.Seconds() : 100.Milliseconds());
            sut.ObserveRead(fromOthers).Should().Be(DoorbellHealth.Unchanged);
        }

        sut.Shortened.Should().BeFalse();
    }

    [Fact]
    public async Task A_wait_with_an_explicit_timeout_is_not_counted()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 5.Seconds(), clock);
        sut.SetNotifyLive(true);

        for (var i = 0; i < 3; i++)
        {
            var wait = sut.WaitAsync(1.Seconds(), CancellationToken.None);
            clock.Advance(1.Seconds());
            await wait.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
            sut.ObserveRead(fromOthers: true).Should().Be(DoorbellHealth.Unchanged, "a retry or a disconnected wait says nothing about the doorbell");
        }
    }

    [Fact]
    public async Task A_shortened_wait_lengthens_again_once_doorbells_deliver_and_no_entry_came_unannounced_for_a_while()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 5.Seconds(), clock);
        sut.SetNotifyLive(true);
        await ShortenAsync(sut, clock);

        sut.Ring();
        await sut.WaitAsync(CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        sut.ObserveRead(fromOthers: true).Should().Be(DoorbellHealth.Unchanged, "an unannounced entry is too recent");
        clock.Advance(60.Seconds());
        sut.Ring();
        await sut.WaitAsync(CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        sut.ObserveRead(fromOthers: true).Should().Be(DoorbellHealth.Recovered);
        sut.Shortened.Should().BeFalse();
        await TimeOutAsync(sut, clock, 5.Seconds());
    }

    [Fact]
    public async Task An_unannounced_entry_on_a_shortened_wait_puts_the_long_wait_off()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 5.Seconds(), clock);
        sut.SetNotifyLive(true);
        await ShortenAsync(sut, clock);

        clock.Advance(59.Seconds());
        await TimeOutAsync(sut, clock, 100.Milliseconds());
        sut.ObserveRead(fromOthers: true);
        clock.Advance(30.Seconds());
        sut.Ring();
        await sut.WaitAsync(CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        sut.ObserveRead(fromOthers: true).Should().Be(DoorbellHealth.Unchanged, "some writer still does not ring");
        sut.Shortened.Should().BeTrue();
    }

    [Fact]
    public async Task A_subscription_taken_again_starts_from_the_long_wait()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 5.Seconds(), clock);
        sut.SetNotifyLive(true);
        await ShortenAsync(sut, clock);

        sut.SetNotifyLive(false);
        sut.SetNotifyLive(true);
        // Losing the subscription left a wake-up pending; the read it causes is the one that reports.
        await sut.WaitAsync(CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        sut.Shortened.Should().BeFalse("the new subscription has missed nothing yet");
        sut.ObserveRead(fromOthers: false).Should().Be(DoorbellHealth.Recovered, "the fallback ended, so it is reported once");
        sut.ObserveRead(fromOthers: false).Should().Be(DoorbellHealth.Unchanged);
        await TimeOutAsync(sut, clock, 5.Seconds());
    }

    [Fact]
    public async Task A_wait_that_ran_under_the_old_subscription_is_not_counted_against_the_new_one()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 5.Seconds(), clock);
        sut.SetNotifyLive(true);
        await TimeOutAsync(sut, clock, 5.Seconds());
        sut.ObserveRead(fromOthers: true);
        await TimeOutAsync(sut, clock, 5.Seconds());

        // The subscription is taken again before the read after that wait is observed.
        sut.SetNotifyLive(false);
        sut.SetNotifyLive(true);

        sut.ObserveRead(fromOthers: true).Should().Be(DoorbellHealth.Unchanged);
        sut.Shortened.Should().BeFalse();
    }

    [Fact]
    public async Task A_fallback_lasts_twelve_notified_intervals_after_the_last_unannounced_entry()
    {
        var clock = NewClock();
        using var sut = new SignalingFetchWaiter(100.Milliseconds(), 5.Seconds(), clock);
        sut.SetNotifyLive(true);
        await ShortenAsync(sut, clock);

        clock.Advance(60.Seconds() - 1.Milliseconds());
        sut.Ring();
        await sut.WaitAsync(CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        sut.ObserveRead(fromOthers: true).Should().Be(DoorbellHealth.Unchanged, "12 notified intervals have not passed yet");

        clock.Advance(1.Milliseconds());
        sut.Ring();
        await sut.WaitAsync(CancellationToken.None).WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        sut.ObserveRead(fromOthers: true).Should().Be(DoorbellHealth.Recovered);
    }

    private static ManualTimeProvider NewClock() => new(DateTimeOffset.UnixEpoch);

    private static async Task ShortenAsync(SignalingFetchWaiter sut, ManualTimeProvider clock)
    {
        for (var i = 0; i < 2; i++)
        {
            await TimeOutAsync(sut, clock, 5.Seconds());
            sut.ObserveRead(fromOthers: true);
        }

        sut.Shortened.Should().BeTrue();
    }

    /// <summary>Starts the idle wait and moves the clock to its end; a wait shorter or longer than <paramref name="interval"/> fails.</summary>
    private static async Task TimeOutAsync(SignalingFetchWaiter sut, ManualTimeProvider clock, TimeSpan interval)
    {
        var wait = sut.WaitAsync(CancellationToken.None);
        clock.Advance(interval - 1.Milliseconds());
        await StillWaitingAsync(wait);
        clock.Advance(1.Milliseconds());
        await wait.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    /// <summary>Gives a wait that should still be pending a real moment to finish wrongly, then asserts it has not.</summary>
    private static async Task StillWaitingAsync(Task wait)
    {
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        wait.IsCompleted.Should().BeFalse();
    }
}
