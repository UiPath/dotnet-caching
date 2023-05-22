using System.Net.Mime;

namespace UiPath.Platform.Caching.CloudEvents;

public class CloudCacheEventFactory : ICacheEventFactory
{
    private readonly Uri? _sourceUri;

    public CloudCacheEventFactory(IOptions<BroadcastOptions> broadcastOptionsAccessor)
    {
        _sourceUri = broadcastOptionsAccessor.Value.SourceUri;
    }

    public ICacheEvent Create(string type, CacheEventData eventData, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(type));
        }

        return new CacheCloudEventWrapper(new()
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Type = type.Trim(),
            Source = _sourceUri,
            DataContentType = MediaTypeNames.Application.Json,
            Data = eventData
        });
    }
}
