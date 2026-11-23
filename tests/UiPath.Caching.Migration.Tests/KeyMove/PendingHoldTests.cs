using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The pending protection of a key whose retry is under way: it ends a window after the retry ends, whichever hold a waiter saw, and the retry set's capacity is exact.
public sealed class PendingHoldTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_waiter_that_awaited_one_hold_keeps_the_replacement_a_newer_write_registered()
    {
        var (pending, clock) = Pending();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        pending.Begin("k", Window, first.Task);
        await SettleAsync();

        // A newer write supersedes the first retry and registers its own while the waiter still awaits the first.
        pending.Begin("k", Window, second.Task);
        first.SetResult();
        await SettleAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        await SettleAsync();

        pending.Count.Should().Be(1, "the replacement retry can still change Redis");

        second.SetResult();
        await UntilAsync(clock, () => pending.Count == 0);
    }

    [Fact]
    public async Task A_hold_that_ends_before_the_timer_would_have_fired_still_gets_its_window()
    {
        var (pending, clock) = Pending();
        var hold = new TaskCompletionSource();
        pending.Begin("k", Window, hold.Task);
        await SettleAsync();

        // The retry gives up just before the first window ends, and its last command may still be in flight.
        clock.Advance(TimeSpan.FromSeconds(4.9));
        await SettleAsync();
        hold.SetResult();
        await SettleAsync();
        clock.Advance(TimeSpan.FromSeconds(0.5));
        await SettleAsync();

        pending.Count.Should().Be(1, "a window of its own starts when the retry ends");

        await UntilAsync(clock, () => pending.Count == 0);
    }

    [Fact]
    public async Task A_key_with_no_retry_retires_when_its_window_ends()
    {
        var (pending, clock) = Pending();
        pending.Begin("k", Window);
        await SettleAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        await SettleAsync();
        pending.Count.Should().Be(1);

        await UntilAsync(clock, () => pending.Count == 0);
    }

    [Fact]
    public async Task The_capacity_of_the_retry_set_holds_under_concurrent_additions()
    {
        var gave = 0;
        using var set = new MoveRetrySet(TimeProvider.System, new KeyMoveOptions { RetryCapacity = 50 }, _ => Interlocked.Increment(ref gave), background: false);

        await Parallel.ForAsync(0, 400, Ct, (i, _) =>
        {
            set.Add("key" + i, DateTimeOffset.UtcNow.AddMinutes(1), _ => Task.FromResult(false));
            return ValueTask.CompletedTask;
        });

        set.Count.Should().Be(50);
        gave.Should().Be(350);
    }

    [Fact]
    public void Stopping_the_retries_drops_what_waits_counts_it_and_refuses_what_comes_later()
    {
        var dropped = new List<string>();
        var set = new MoveRetrySet(TimeProvider.System, new KeyMoveOptions(), _ => { }, background: false, dropped.Add);
        set.Add("a", DateTimeOffset.UtcNow.AddDays(1), _ => Task.FromResult(false));
        var settled = set.SettledAsync("a");

        set.Stop();
        set.Add("b", DateTimeOffset.UtcNow.AddDays(1), _ => Task.FromResult(false));

        dropped.Should().BeEquivalentTo("a", "b");
        settled!.IsCompleted.Should().BeTrue();
        set.Count.Should().Be(0);
    }

    [Fact]
    public async Task Stopping_leaves_an_attempt_that_is_running_to_finish_and_settles_its_key_only_when_it_returns()
    {
        var dropped = new List<string>();
        var set = new MoveRetrySet(TimeProvider.System, new KeyMoveOptions(), _ => { }, background: false, dropped.Add);
        var running = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        set.Add("active", DateTimeOffset.UtcNow.AddDays(1), async _ =>
        {
            started.SetResult();
            await running.Task;
            return true;
        });
        set.Add("waiting", DateTimeOffset.UtcNow.AddDays(1), _ => Task.FromResult(false));
        var activeSettled = set.SettledAsync("active")!;
        var waitingSettled = set.SettledAsync("waiting")!;
        var pass = set.RunDueAsync(all: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var stopping = set.StopAsync();

        waitingSettled.IsCompleted.Should().BeTrue("a waiting retry is dropped at once");
        dropped.Should().Equal("waiting");
        activeSettled.IsCompleted.Should().BeFalse("the attempt that is running is still writing");
        stopping.IsCompleted.Should().BeFalse();

        running.SetResult();
        await pass;
        await stopping.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        activeSettled.IsCompleted.Should().BeTrue();
        set.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_key_is_settled_only_when_every_attempt_for_it_has_returned_including_a_superseded_one_that_was_running()
    {
        var set = new MoveRetrySet(TimeProvider.System, new KeyMoveOptions(), _ => { }, background: false);
        var running = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        set.Add("k", DateTimeOffset.UtcNow.AddDays(1), async _ =>
        {
            started.SetResult();
            await running.Task;
            return true;
        });
        var first = set.SettledAsync("k")!;
        var pass = set.RunDueAsync(all: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // A newer write supersedes the running attempt and registers its own, which the stop then drops.
        set.Supersede("k");
        set.Add("k", DateTimeOffset.UtcNow.AddDays(1), _ => Task.FromResult(false));
        var second = set.SettledAsync("k")!;
        var stopping = set.StopAsync();

        second.Should().BeSameAs(first, "one task serves the key while any attempt for it is outstanding");
        second.IsCompleted.Should().BeFalse("the first attempt is still running");
        set.OutstandingKeys.Should().Equal("k");

        running.SetResult();
        await pass;
        await stopping.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        second.IsCompleted.Should().BeTrue();
        set.OutstandingKeys.Should().BeEmpty();
    }

    private static (PendingMutations Pending, ManualTimeProvider Clock) Pending()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var core = new RouterCore(clock, _ => true, NullLogger.Instance);
        return (new PendingMutations(core, "c", _ => ValueTask.CompletedTask, _ => { }), clock);
    }

    private static Task SettleAsync() => Task.Delay(60, Ct);

    private static async Task UntilAsync(ManualTimeProvider clock, Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, Ct);
        }

        done().Should().BeTrue();
    }
}
