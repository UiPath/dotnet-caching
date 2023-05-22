namespace UiPath.Platform.Caching.Broadcast;

public interface ICacheEventFactory
{
    ICacheEvent Create(string type, CacheEventData eventData, string? id = null);
}
