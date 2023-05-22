namespace UiPath.Platform.Caching.Broadcast;

public sealed class CacheEventFactory : ICacheEventFactory
{
    private readonly Uri? _sourceUri;

    public CacheEventFactory(IOptions<BroadcastOptions> broadcastOptionsAccessor)
    {
        _sourceUri = broadcastOptionsAccessor.Value.SourceUri;
    }

    public ICacheEvent Create(string type, CacheEventData eventData, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(type));
        }

        return new CacheEvent
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Type = type.Trim(),
            Source = _sourceUri,
            Data = eventData
        };
    }
}
