using System.Net.Mime;
using UiPath.Platform.Caching.Broadcast;

namespace UiPath.Platform.Caching.CloudEvents;

public class ClearCacheEventFactory : IClearCacheEventFactory
{
    internal static readonly Uri MachineUri = new($"urn:{Environment.MachineName}");

    public IClearCacheEvent Create(ClearCacheEventData eventData, Uri? sourceUri = null, string? id = null) =>
        new CacheClearCloudEventWrapper(new()
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Type = CloudEventTypes.ClearCache,
            Source = sourceUri ?? MachineUri,
            DataContentType = MediaTypeNames.Application.Json,
            Data = eventData
        });

}
