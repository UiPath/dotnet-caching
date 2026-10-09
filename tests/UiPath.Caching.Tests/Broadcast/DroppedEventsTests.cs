using Microsoft.Extensions.Logging.Abstractions;

namespace UiPath.Caching.Tests.Broadcast;

public class DroppedEventsTests
{
    [Fact]
    public async Task Drops_while_an_expiry_runs_are_folded_into_one_more()
    {
        var runs = 0;
        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        void Expire()
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                running.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        }

        var sut = new DroppedEvents("topic", Expire, NullLogger.Instance);

        sut.Dropped();
        running.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue();
        for (var i = 0; i < 100; i++)
        {
            sut.Dropped();
        }

        release.Set();
        (await Eventually(() => Volatile.Read(ref runs) >= 2)).Should().BeTrue();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Volatile.Read(ref runs).Should().Be(2, "the drops during the first expiry need one more, not one each");
    }

    [Fact]
    public async Task A_drop_during_an_expiry_waits_for_it_rather_than_running_alongside()
    {
        var inside = 0;
        var most = 0;
        using var release = new ManualResetEventSlim();
        void Expire()
        {
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref most, now);
            release.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Decrement(ref inside);
        }

        var sut = new DroppedEvents("topic", Expire, NullLogger.Instance);
        sut.Dropped();
        (await Eventually(() => Volatile.Read(ref inside) == 1)).Should().BeTrue();
        sut.Dropped();
        sut.Dropped();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var duringFirst = Volatile.Read(ref most);
        release.Set();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        duringFirst.Should().Be(1, "the rerun waits for the expiry in progress");
        Volatile.Read(ref most).Should().Be(1);
    }

    [Fact]
    public async Task An_expiry_that_throws_is_logged_and_the_next_drop_still_expires()
    {
        var runs = 0;
        void Expire()
        {
            Interlocked.Increment(ref runs);
            throw new InvalidOperationException("boom");
        }

        var sut = new DroppedEvents("topic", Expire, NullLogger.Instance);

        sut.Dropped();
        (await Eventually(() => Volatile.Read(ref runs) == 1)).Should().BeTrue();
        sut.Dropped();

        (await Eventually(() => Volatile.Read(ref runs) == 2)).Should().BeTrue("a throwing expiry must not leave the next drop pending for good");
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return false;
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
