namespace UiPath.Caching.Tests.Broadcast;

public class ChannelHelperTests
{
    [Theory]
    [InlineData(System.Threading.Channels.BoundedChannelFullMode.DropWrite, 2)]
    [InlineData(System.Threading.Channels.BoundedChannelFullMode.DropOldest, 1)]
    [InlineData(System.Threading.Channels.BoundedChannelFullMode.DropNewest, 1)]
    public void A_full_bounded_channel_reports_what_it_drops(System.Threading.Channels.BoundedChannelFullMode mode, int expected)
    {
        var dropped = new List<int>();
        var channel = ChannelHelper.Create<int>(unbounded: false, capacity: 1, mode, dropped.Add);

        channel.Writer.TryWrite(1).Should().BeTrue();
        channel.Writer.TryWrite(2).Should().BeTrue();

        dropped.Should().Equal(expected);
    }

    [Theory]
    [InlineData(10, 100, 100)]
    [InlineData(2048, 4096, 4096)]
    [InlineData(100, 10, 100)]
    [InlineData(50, 50, 50)]
    [InlineData(1, 1, 1)]
    public void CalculateBoundedCapacity_returns_max_when_consumerCapacity_is_positive(int consumerCapacity, int pollBatchSize, int expected)
    {
        ChannelHelper.CalculateBoundedCapacity(consumerCapacity, pollBatchSize).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 50, 50)]
    [InlineData(0, 4096, 4096)]
    public void CalculateBoundedCapacity_falls_back_to_pollBatchSize_when_consumerCapacity_is_zero(int consumerCapacity, int pollBatchSize, int expected)
    {
        ChannelHelper.CalculateBoundedCapacity(consumerCapacity, pollBatchSize).Should().Be(expected);
    }

    [Theory]
    [InlineData(-1, 4096)]
    [InlineData(-100, 1)]
    public void CalculateBoundedCapacity_ignores_pollBatchSize_when_consumerCapacity_is_negative(int consumerCapacity, int pollBatchSize)
    {
        var act = () => ChannelHelper.CalculateBoundedCapacity(consumerCapacity, pollBatchSize);
        act.Should().NotThrow();
    }
}
