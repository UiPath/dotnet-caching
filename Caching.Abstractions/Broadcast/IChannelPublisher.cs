namespace UiPath.Platform.Caching.Broadcast;

public interface IChannelPublisher
{
    Task PublishAsync(Channel channel, IClearCacheEvent clearCacheEvent, CancellationToken token = default);
}
