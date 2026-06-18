namespace UiPath.Caching.Broadcast;

public record CacheEventData(
    string Key,
    IDictionary<string, object?>? Properties = null
);
