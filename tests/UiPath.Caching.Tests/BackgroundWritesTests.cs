using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests;

public class BackgroundWritesTests
{
    [Fact]
    public void Before_the_first_background_write_nothing_is_tracked_and_a_read_spanning_it_is_not_kept()
    {
        var writes = new BackgroundWrites(8, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, Substitute.For<ICachingTelemetryProvider>(), TimeProvider.System);
        var mark = writes.Mark("k");

        using (var turn = writes.Enter("k"))
        {
            turn.Previous.IsCompleted.Should().BeTrue();
        }

        writes.Active.Should().BeFalse();
        writes.Fill("k", mark, 0, static _ => true).Should().BeTrue();
        writes.TryReserve().Should().BeTrue();
        writes.Active.Should().BeTrue();
        writes.Fill("k", mark, 0, static _ => true).Should().BeNull("the read began before the order was tracked");
        writes.Release();
    }

    [Fact]
    public void A_fill_tells_a_change_since_its_mark_from_a_local_write_that_refused()
    {
        var writes = new BackgroundWrites(8, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, Substitute.For<ICachingTelemetryProvider>(), TimeProvider.System);
        writes.TryReserve().Should().BeTrue();
        writes.Release();
        var mark = writes.Mark("k");

        writes.Fill("k", mark, 0, static _ => false).Should().BeFalse();
        writes.Apply("k", 0, static _ => true);
        writes.Fill("k", mark, 0, static _ => true).Should().BeNull();
        writes.Fill("k", writes.Mark("k"), 0, static _ => true).Should().BeTrue();
    }

    [Fact]
    public async Task A_background_write_whose_local_write_a_later_one_of_the_key_overtook_leaves_the_later_value()
    {
        var local = new List<string>();
        using var firstIssued = new ManualResetEventSlim();
        using var secondDone = new ManualResetEventSlim();
        var writes = new BackgroundWrites(8, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, Substitute.For<ICachingTelemetryProvider>(), TimeProvider.System, onIssued: _ =>
        {
            if (!firstIssued.IsSet)
            {
                firstIssued.Set();
                secondDone.Wait(TimeSpan.FromSeconds(5));
            }
        });
        writes.TryReserve().Should().BeTrue();
        writes.TryReserve().Should().BeTrue();
        bool Record(string value)
        {
            lock (local)
            {
                local.Add(value);
            }

            return true;
        }

        var first = Task.Run(() => writes.Run("k", "v1", Record, static (_, _) => ValueTask.CompletedTask), TestContext.Current.CancellationToken);
        firstIssued.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).Should().BeTrue();
        writes.Run("k", "v2", Record, static (_, _) => ValueTask.CompletedTask);
        secondDone.Set();

        (await first).Should().BeTrue("the newer value is the one kept");
        local.Should().Equal("v2");
        await writes.DrainAsync();
    }
}
