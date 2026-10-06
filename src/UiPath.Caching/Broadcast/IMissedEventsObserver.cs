namespace UiPath.Caching.Broadcast;

/// <summary>
/// An observer a subject can expire, without ending its subscription, when events meant for it were lost. A subject may
/// call it while an <see cref="IObserver{T}.OnNext"/> runs on another thread, and more than once for one loss when the
/// observer joins as it is reported, so expiring must be idempotent.
/// </summary>
public interface IMissedEventsObserver
{
    void OnEventsMissed(MissedEventsReason reason);
}
