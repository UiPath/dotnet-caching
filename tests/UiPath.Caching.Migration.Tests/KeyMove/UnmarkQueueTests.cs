using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

public sealed class UnmarkQueueTests
{
    [Fact]
    public void A_full_queue_drops_the_next_marker_and_counts_it_for_its_cache()
    {
        using var meter = new MeterCollector("cache-a");
        using var queue = new UnmarkQueue(new ManualTimeProvider(DateTimeOffset.UnixEpoch), new KeyMoveOptions { UnmarkQueueMax = 2 }, () => null, background: false);

        queue.Enqueue("ctl", "sm:1", "v|1", "cache-a", "g").Should().BeTrue();
        queue.Enqueue("ctl", "sm:2", "v|2", "cache-a", "g").Should().BeTrue();
        queue.Enqueue("ctl", "sm:3", "v|3", "cache-a", "g").Should().BeFalse();

        queue.Count.Should().Be(2);
        meter.Count("cache.keymove.unmark_dropped").Should().Be(1);
    }

    [Fact]
    public void Saturation_starts_above_half_and_ends_below_a_quarter()
    {
        using var queue = new UnmarkQueue(new ManualTimeProvider(DateTimeOffset.UnixEpoch), new KeyMoveOptions { UnmarkQueueMax = 8 }, () => null, background: false);

        for (var i = 0; i < 4; i++)
        {
            queue.Enqueue("ctl", $"sm:{i}", "v", "c", "g");
        }

        queue.Saturated.Should().BeFalse();
        queue.Enqueue("ctl", "sm:4", "v", "c", "g");
        queue.Saturated.Should().BeTrue();
    }
}
