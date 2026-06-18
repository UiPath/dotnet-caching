using System.Threading.Channels;

namespace UiPath.Caching.Broadcast;

internal static class ChannelHelper
{
    public static Channel<T> Create<T>(bool unbounded, int capacity, BoundedChannelFullMode fullMode) => unbounded
            ? Channel.CreateUnbounded<T>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            })
            : Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
            {
                FullMode = fullMode,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });

    public static int CalculateBoundedCapacity(int consumerCapacity, int pollBatchSize) =>
        consumerCapacity > 0
            ? Math.Max(consumerCapacity, pollBatchSize)
            : pollBatchSize;
}
