namespace UiPath.Caching.Broadcast;

public interface ITopic<T> : IDisposable
    where T : IEvent
{
    TopicKey TopicKey { get; }

    EventHandler? OnDisposed { get; set; }

    IDisposable Subscribe(IObserver<T> observer);

    ValueTask<bool> PublishAsync(T @event, CancellationToken token = default);
}
