using System.Threading.Channels;

namespace UiPath.Caching.Broadcast;

internal static class ChannelHelper
{
    /// <summary>A bounded channel reports each item a full channel drops to <paramref name="itemDropped"/>.</summary>
    public static Channel<T> Create<T>(bool unbounded, int capacity, BoundedChannelFullMode fullMode, Action<T>? itemDropped = null)
    {
        if (unbounded)
        {
            return Channel.CreateUnbounded<T>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        }

        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = fullMode,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        };
        return Channel.CreateBounded(options, itemDropped);
    }

    public static int CalculateBoundedCapacity(int consumerCapacity, int pollBatchSize) =>
        consumerCapacity > 0
            ? Math.Max(consumerCapacity, pollBatchSize)
            : pollBatchSize;
}
