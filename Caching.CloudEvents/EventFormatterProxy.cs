namespace UiPath.Platform.Caching.CloudEvents;

public class CacheEventFormatterProxy : IEventFormatterProxy<ICacheEvent>
{
    private readonly CloudEventFormatter _formatter;

    public CacheEventFormatterProxy(CloudEventFormatter formatter) =>
        _formatter = formatter;

    public ICacheEvent? Decode(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty)
        {
            return null;
        }

        return new CacheCloudEventWrapper(_formatter.DecodeStructuredModeMessage(body, null, null));
    }

    public ReadOnlyMemory<byte> Encode(ICacheEvent pubSubEvent) =>
        pubSubEvent is CacheCloudEventWrapper wrapper
            ? _formatter.EncodeStructuredModeMessage(wrapper.CloudEvent, out _)
            : ReadOnlyMemory<byte>.Empty;
}
