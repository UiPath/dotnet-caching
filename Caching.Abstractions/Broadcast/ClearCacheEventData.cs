namespace UiPath.Platform.Caching.Broadcast;

public record ClearCacheEventData(string Key, string[]? Fields = null);
