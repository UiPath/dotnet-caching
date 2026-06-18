namespace UiPath.Caching.CloudEvents;

internal sealed class CacheCloudEventWrapper : ICacheEvent
{
    public CacheCloudEventWrapper(CloudEvent cloudEvent)
    {
        CloudEvent = cloudEvent;
        Data = CloudEvent.Data as CacheEventData;
    }
    internal CloudEvent CloudEvent { get; }

    public bool IsValid() => CloudEvent.IsValid && !string.IsNullOrWhiteSpace(Data?.Key);

    public string? Id => CloudEvent.Id;

    public Uri? Source => CloudEvent.Source;

    public CacheEventData? Data { get; }

    public string? Type => CloudEvent.Type;

    public string? TransportId { get; private set; }

    public string? Key => Data?.Key;

    public void AttachTransportId(string? transportId)
    {
        if (TransportId != null)
        {
            throw new InvalidOperationException();
        }

        TransportId = transportId;
    }
}
