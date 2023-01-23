namespace UiPath.Platform.Caching.Tests.Broadcast;

public class TestClearCacheEvent : IClearCacheEvent
{
    public bool Valid { get; set; } = true;

    public string? Id { get; set; }

    public Uri? Source { get; set; }

    public ClearCacheEventData? Data { get; set; }

    public bool IsValid() => Valid;
}
