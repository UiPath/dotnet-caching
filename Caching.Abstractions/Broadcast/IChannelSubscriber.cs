namespace UiPath.Platform.Caching.Broadcast;

public interface IChannelSubscriber<T>
{
    IDisposable Subscribe(Channel channel, IObserver<T> observer);
}
