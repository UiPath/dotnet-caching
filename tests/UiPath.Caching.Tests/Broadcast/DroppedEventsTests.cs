using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace UiPath.Caching.Tests.Broadcast;

public class DroppedEventsTests
{
    [Fact]
    public async Task Drops_while_an_expiry_runs_are_folded_into_one_more()
    {
        var runs = 0;
        var completed = Channel.CreateUnbounded<int>();
        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        void Expire()
        {
            var run = Interlocked.Increment(ref runs);
            if (run == 1)
            {
                running.Set();
                release.Wait(TestContext.Current.CancellationToken);
            }
            completed.Writer.TryWrite(run);
        }

        var sut = new DroppedEvents("topic", Expire, NullLogger.Instance);

        sut.Dropped();
        running.Wait(TestContext.Current.CancellationToken);
        for (var i = 0; i < 100; i++)
        {
            sut.Dropped();
        }

        release.Set();
        (await completed.Reader.ReadAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await completed.Reader.ReadAsync(TestContext.Current.CancellationToken)).Should().Be(2);

        Volatile.Read(ref runs).Should().Be(2, "the drops during the first expiry need one more, not one each");
    }

    [Fact]
    public async Task A_drop_during_an_expiry_waits_for_it_rather_than_running_alongside()
    {
        var inside = 0;
        var most = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = Channel.CreateUnbounded<bool>();
        using var release = new ManualResetEventSlim();
        void Expire()
        {
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref most, now);
            entered.TrySetResult();
            release.Wait(TestContext.Current.CancellationToken);
            Interlocked.Decrement(ref inside);
            completed.Writer.TryWrite(true);
        }

        var sut = new DroppedEvents("topic", Expire, NullLogger.Instance);
        sut.Dropped();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        sut.Dropped();
        sut.Dropped();
        var duringFirst = Volatile.Read(ref most);
        release.Set();
        await completed.Reader.ReadAsync(TestContext.Current.CancellationToken);
        await completed.Reader.ReadAsync(TestContext.Current.CancellationToken);

        duringFirst.Should().Be(1, "the rerun waits for the expiry in progress");
        Volatile.Read(ref most).Should().Be(1);
    }

    [Fact]
    public async Task An_expiry_that_throws_is_logged_and_the_next_drop_still_expires()
    {
        var runs = 0;
        var completed = Channel.CreateUnbounded<int>();
        void Expire()
        {
            completed.Writer.TryWrite(Interlocked.Increment(ref runs));
            throw new InvalidOperationException("boom");
        }

        var sut = new DroppedEvents("topic", Expire, NullLogger.Instance);

        sut.Dropped();
        (await completed.Reader.ReadAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        sut.Dropped();

        (await completed.Reader.ReadAsync(TestContext.Current.CancellationToken)).Should().Be(2, "a throwing expiry must not leave the next drop pending for good");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
