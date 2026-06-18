namespace UiPath.Caching.Broadcast;

public interface ICacheEvent : IEvent
{
    CacheEventData? Data { get; }
}
