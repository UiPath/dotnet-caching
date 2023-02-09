using CloudNative.CloudEvents;
using UiPath.Platform.Caching.Broadcast;

namespace UiPath.Platform.Caching.CloudEvents;

public class CacheClearEventFormatterProxy : IEventFormatterProxy<IClearCacheEvent>
{
    private readonly CloudEventFormatter _formatter;

    public CacheClearEventFormatterProxy(CloudEventFormatter formatter) =>
        _formatter = formatter;

    public IClearCacheEvent? Decode(ReadOnlyMemory<byte> body) =>
        new CacheClearCloudEventWrapper(_formatter.DecodeStructuredModeMessage(body, null, null));

    public ReadOnlyMemory<byte> Encode(IClearCacheEvent pubSubEvent) =>
        pubSubEvent is CacheClearCloudEventWrapper wrapper
            ? _formatter.EncodeStructuredModeMessage(wrapper.CloudEvent, out _)
            : ReadOnlyMemory<byte>.Empty;
}
