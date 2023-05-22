namespace UiPath.Platform.Caching.CloudEvents;

internal sealed class CacheCloudEventWrapper : ICacheEvent
{
    public CacheCloudEventWrapper(CloudEvent cloudEvent)
    {
        CloudEvent = cloudEvent;
        Data = CloudEvent.Data as CacheEventData;
    }
    internal CloudEvent CloudEvent { get; }

    public bool IsValid() =>
        CloudEvent.IsValid && KnownEventTypes.IsKnown(CloudEvent.Type);

    public string? Id => CloudEvent.Id;

    public Uri? Source => CloudEvent.Source;

    public CacheEventData? Data { get; }

    public string? Type => CloudEvent.Type;
}
