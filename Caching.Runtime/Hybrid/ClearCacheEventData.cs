namespace UiPath.Platform.Caching.Hybrid;

public record ClearCacheEventData(string Key, string[]? Fields = null);
