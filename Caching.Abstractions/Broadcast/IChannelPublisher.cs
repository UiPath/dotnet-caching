namespace UiPath.Platform.Caching.Broadcast;

public interface IChannelPublisher<in T>
{
    Task PublishAsync(Channel channel, T pubSubEvent, CancellationToken token = default);
}
