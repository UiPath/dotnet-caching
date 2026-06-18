namespace UiPath.Caching.Broadcast;

public interface IEventSubject<T> : IDisposable where T : IEvent
{
    IDisposable Subscribe(IObserver<T> observer);

    void OnNext(T value);

    void OnCompleted();
}
