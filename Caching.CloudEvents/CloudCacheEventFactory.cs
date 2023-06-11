using System.Collections.Immutable;
using System.Net.Mime;

namespace UiPath.Platform.Caching.CloudEvents;

public class CloudCacheEventFactory : ICacheEventFactory
{
    private readonly Uri? _sourceUri;
    private readonly ISet<string> _knownEventTypes;

    public CloudCacheEventFactory(IOptions<CacheOptions> optionsAccessor)
    {
        _sourceUri = optionsAccessor.Value.SourceUri ?? CacheOptions.MachineUri;
        _knownEventTypes = typeof(KnownEventTypes).GetAllPublicConstantValues<string>().ToImmutableHashSet(StringComparer.InvariantCultureIgnoreCase);
    }

    public ICacheEvent Create(string cacheName, string eventType, CacheEventData eventData, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(eventType));
        }

        return new CacheCloudEventWrapper(new()
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Type = eventType.Trim(),
            Source = _sourceUri,
            DataContentType = MediaTypeNames.Application.Json,
            Data = eventData
        });
    }

    public bool IsKnown(string? eventType) =>
         !string.IsNullOrEmpty(eventType) && _knownEventTypes.Contains(eventType);
}
