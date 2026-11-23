using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public sealed class ConnectionChangesSideTests
{
    [Fact]
    public void An_event_is_counted_for_its_side_and_in_the_combined_counter()
    {
        var src = new EventCountingMultiplexer();
        var dst = new EventCountingMultiplexer();
        using var changes = new ConnectionChanges(src.Multiplexer, dst.Multiplexer);
        var (total, srcMark, dstMark) = (changes.Current, changes.SourceCurrent, changes.DestinationCurrent);

        changes.Observe(RecordSide.Dst);

        changes.Current.Should().Be(total + 1);
        changes.ChangedSince(RecordSide.Dst, dstMark).Should().BeTrue();
        changes.ChangedSince(RecordSide.Src, srcMark).Should().BeFalse();
        changes.ChangedSince(total).Should().BeTrue();
    }

    [Fact]
    public void A_dedicated_multiplexer_is_counted_for_the_side_it_serves_and_detached_with_the_counter()
    {
        var src = new EventCountingMultiplexer();
        var dst = new EventCountingMultiplexer();
        var dedicated = new EventCountingMultiplexer();
        var changes = new ConnectionChanges(src.Multiplexer, dst.Multiplexer);

        changes.Watch(dedicated.Multiplexer, RecordSide.Src);
        dedicated.Attached.Should().Be(1);
        changes.Dispose();

        dedicated.Attached.Should().Be(0);
    }
}
