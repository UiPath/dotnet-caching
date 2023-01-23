using CloudNative.CloudEvents;
using UiPath.Platform.Caching.Broadcast;

namespace UiPath.Platform.Caching.CloudEvents;

public class EventFormatterProxy : IEventFormatterProxy
{
    private readonly CloudEventFormatter _formatter;

    public EventFormatterProxy(CloudEventFormatter formatter) =>
        _formatter = formatter;

    public IClearCacheEvent? Decode(ReadOnlyMemory<byte> body) =>
        new CloudEventWrapper(_formatter.DecodeStructuredModeMessage(body, null, null));

    public ReadOnlyMemory<byte> Encode(IClearCacheEvent clearCacheEvent) =>
        clearCacheEvent is CloudEventWrapper wrapper
            ? _formatter.EncodeStructuredModeMessage(wrapper.CloudEvent, out _)
            : ReadOnlyMemory<byte>.Empty;
}
