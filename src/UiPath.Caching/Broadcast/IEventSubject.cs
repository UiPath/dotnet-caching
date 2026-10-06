namespace UiPath.Caching.Broadcast;

public interface IEventSubject<T> : IDisposable where T : IEvent
{
    IDisposable Subscribe(IObserver<T> observer);

    void OnNext(T value);

    void OnCompleted();

    /// <summary>Expire what the observers keep, without ending their subscriptions: events meant for them were lost.</summary>
    void Invalidate(MissedEventsReason reason);
}
