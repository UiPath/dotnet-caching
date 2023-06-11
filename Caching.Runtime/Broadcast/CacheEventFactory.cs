using System.Collections.Immutable;

namespace UiPath.Platform.Caching.Broadcast;

public sealed class CacheEventFactory : ICacheEventFactory
{
    private readonly Uri? _sourceUri;
    private readonly ISet<string> _knownEventTypes;

    public CacheEventFactory(IOptions<CacheOptions> optionsAccessor)
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

        return new CacheEvent
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Type = eventType.Trim(),
            Source = _sourceUri,
            Data = eventData
        };
    }

    public bool IsKnown(string? eventType) =>
         !string.IsNullOrEmpty(eventType) && _knownEventTypes.Contains(eventType);
}
