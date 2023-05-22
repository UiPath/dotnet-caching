namespace UiPath.Platform.Caching.Broadcast;

public interface ICacheEvent : IPubSubEvent
{
    CacheEventData? Data { get; }
}
