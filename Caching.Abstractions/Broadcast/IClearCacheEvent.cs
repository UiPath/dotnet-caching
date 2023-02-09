namespace UiPath.Platform.Caching.Broadcast;

public interface IClearCacheEvent : IPubSubEvent
{
    ClearCacheEventData? Data { get; }
}

public interface IClearCacheEventFactory
{
    IClearCacheEvent Create(ClearCacheEventData eventData, Uri? sourceUri = null, string? id = null);
}
