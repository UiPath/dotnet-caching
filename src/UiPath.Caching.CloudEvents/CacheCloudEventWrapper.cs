using System.Net.Mime;

namespace UiPath.Caching.CloudEvents;

internal sealed class CacheCloudEventWrapper : ICacheEvent
{
    private readonly string? _id;
    private readonly string? _type;
    private readonly Uri? _source;
    private CloudEvent? _cloudEvent;

    public CacheCloudEventWrapper(CloudEvent cloudEvent)
    {
        _cloudEvent = cloudEvent;
        Data = cloudEvent.Data as CacheEventData;
    }

    public CacheCloudEventWrapper(string id, string type, Uri source, CacheEventData data)
    {
        _id = id;
        _type = type;
        _source = source;
        Data = data;
    }

    public string? Id => _cloudEvent is { } cloudEvent ? cloudEvent.Id : _id;

    public Uri? Source => _cloudEvent is { } cloudEvent ? cloudEvent.Source : _source;

    public CacheEventData? Data { get; }

    public string? Type => _cloudEvent is { } cloudEvent ? cloudEvent.Type : _type;

    public string? TransportId { get; private set; }

    public string? Key => Data?.Key;

    internal CloudEvent CloudEvent => _cloudEvent ??= new()
    {
        Id = _id,
        Type = _type,
        Source = _source,
        DataContentType = MediaTypeNames.Application.Json,
        Data = Data,
    };

    public bool IsValid() => (_cloudEvent?.IsValid ?? true) && !string.IsNullOrWhiteSpace(Data?.Key);

    public void AttachTransportId(string? transportId)
    {
        if (TransportId != null)
        {
            throw new InvalidOperationException();
        }

        TransportId = transportId;
    }
}
