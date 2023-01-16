namespace UiPath.Platform.Caching;

public interface ICacheEntry
{
    DateTimeOffset Expiration { get; }

    IDictionary<string, string?>? ExtendedProperties { get; }

    ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? extendedProperties = null);
}
