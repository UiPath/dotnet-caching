namespace UiPath.Platform.Caching.Broadcast;

public interface ICacheEventFactory
{
    bool IsKnown(string? eventType);

    ICacheEvent Create(string cacheName, string eventType, CacheEventData eventData, string? id = null);
}
