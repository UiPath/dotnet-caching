namespace UiPath.Platform.Caching.Broadcast;

public interface IClearCacheEvent
{
    string? Id { get; }

    Uri? Source { get; }

    ClearCacheEventData? Data { get; }

    bool IsValid();
}

public interface IClearCacheEventFactory
{
    IClearCacheEvent Create(ClearCacheEventData eventData, Uri? sourceUri = null, string? id = null);
}
