namespace UiPath.Platform.Caching.Broadcast;

public interface ITopic<T> : IDisposable
    where T : IEvent
{
    TopicKey TopicKey { get; }

    IDisposable Subscribe(IObserver<T> observer);

    Task<bool> PublishAsync(T @event, CancellationToken token = default);
}
