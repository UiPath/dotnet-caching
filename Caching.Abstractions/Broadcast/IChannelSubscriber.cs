namespace UiPath.Platform.Caching.Broadcast;

public interface IChannelSubscriber
{
    IDisposable Subscribe(Channel channel, IObserver<IClearCacheEvent> observer);
}
