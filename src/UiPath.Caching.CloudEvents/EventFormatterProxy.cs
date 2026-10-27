using CloudNative.CloudEvents.SystemTextJson;

namespace UiPath.Caching.CloudEvents;

public class CacheEventFormatterProxy(CloudEventFormatter formatter) : IEventFormatterProxy<ICacheEvent>
{
    private FastCloudEventCodec? _fast;

    public ICacheEvent? Decode(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty)
        {
            return null;
        }

        if (_fast is not null && _fast.TryDecode(body.Span, out var decoded))
        {
            return decoded;
        }

        return new CacheCloudEventWrapper(formatter.DecodeStructuredModeMessage(body, null, null));
    }

    public ReadOnlyMemory<byte> Encode(ICacheEvent pubSubEvent)
    {
        if (pubSubEvent is not CacheCloudEventWrapper wrapper)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (_fast is not null && _fast.TryEncode(wrapper.CloudEvent, out var encoded))
        {
            return encoded;
        }

        return formatter.EncodeStructuredModeMessage(wrapper.CloudEvent, out _);
    }

    internal static CacheEventFormatterProxy CreateDefault() =>
        new(new JsonEventFormatter<CacheEventData>()) { _fast = new FastCloudEventCodec() };
}
