namespace UiPath.Platform.Caching.Broadcast;

public sealed class CacheEvent : ICacheEvent
{
    public string? Id { get; set; }

    public Uri? Source { get; set; }

    public CacheEventData? Data { get; set; }

    public string? Type { get; set; }

    public bool IsValid() => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Type) && Source != null && !string.IsNullOrWhiteSpace(Data?.Key);
}
