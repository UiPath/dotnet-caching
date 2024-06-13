namespace UiPath.Platform.Caching.CloudEvents;

public class CacheEventFormatterProxy(CloudEventFormatter formatter) : IEventFormatterProxy<ICacheEvent>
{
    public ICacheEvent? Decode(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty)
        {
            return null;
        }

        return new CacheCloudEventWrapper(formatter.DecodeStructuredModeMessage(body, null, null));
    }

    public ReadOnlyMemory<byte> Encode(ICacheEvent pubSubEvent) =>
        pubSubEvent is CacheCloudEventWrapper wrapper
            ? formatter.EncodeStructuredModeMessage(wrapper.CloudEvent, out _)
            : ReadOnlyMemory<byte>.Empty;
}
