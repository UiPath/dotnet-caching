namespace UiPath.Platform.Caching.Broadcast;

public interface ICacheEvent : IEvent
{
    CacheEventData? Data { get; }
}
