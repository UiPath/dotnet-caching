namespace UiPath.Platform.Caching.Tests.Broadcast;

public class TestCacheEvent : ICacheEvent
{
    public bool Valid { get; set; } = true;

    public string? Id { get; set; }

    public Uri? Source { get; set; }

    public CacheEventData? Data { get; set; }

    public string? Type { get; set; }

    public string? TransportId {get; set;}

    public string? Key => Data?.Key;

    public void AttachTransportId(string? transportId)
    {
        TransportId = transportId;
    }

    public bool IsValid() => Valid;
}
