namespace UiPath.Platform.Caching.Broadcast.Redis;

public interface IRedisChannelPublisher<in T> : IChannelPublisher<T>
    where T : class, IPubSubEvent
{
}
