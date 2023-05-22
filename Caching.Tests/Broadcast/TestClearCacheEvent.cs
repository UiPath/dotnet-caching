namespace UiPath.Platform.Caching.Tests.Broadcast;

public class TestClearCacheEvent : ICacheEvent
{
    public bool Valid { get; set; } = true;

    public string? Id { get; set; }

    public Uri? Source { get; set; }

    public CacheEventData? Data { get; set; }

    public string? Type { get; set; }

    public bool IsValid() => Valid;
}
