using CloudNative.CloudEvents;
using UiPath.Platform.Caching.Broadcast;

namespace UiPath.Platform.Caching.CloudEvents;

internal sealed class CloudEventWrapper : IClearCacheEvent
{
    public CloudEventWrapper(CloudEvent cloudEvent)
    {
        CloudEvent = cloudEvent;
        Data = CloudEvent.Data as ClearCacheEventData;
    }
    internal CloudEvent CloudEvent { get; private set; }

    public bool IsValid()
    {
        if (!CloudEvent.IsValid)
        {
            return false;
        }

        return string.Equals(CloudEvent.Type, CloudEventTypes.ClearCache, StringComparison.InvariantCultureIgnoreCase)
            && CloudEvent.Data is ClearCacheEventData data && data != null;
    }

    public string? Id => CloudEvent.Id;

    public Uri? Source => CloudEvent.Source;

    public ClearCacheEventData? Data { get; }
}
