using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration;

/// <summary>Evicts on a peer's key event; anything that says events were or may have been missed clears the cache's local entries.</summary>
internal sealed class PeerObserver(Uri ownSource, Action<string> evict, Action clear) : IObserver<ICacheEvent>, IMissedEventsObserver
{
    public void OnNext(ICacheEvent value)
    {
        if (value.SameSource(ownSource))
        {
            return;
        }

        if (!string.Equals(value.Type, KnownEventTypes.TopicInvalidated, StringComparison.OrdinalIgnoreCase) && value.Key is { Length: > 0 } key)
        {
            evict(key);
        }
        else
        {
            clear();
        }
    }

    public void OnEventsMissed(MissedEventsReason reason) => clear();

    public void OnError(Exception error) => clear();

    public void OnCompleted() => clear();
}
