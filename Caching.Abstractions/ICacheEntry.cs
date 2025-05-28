namespace UiPath.Platform.Caching;

public interface ICacheEntry
{
    DateTimeOffset Expiration { get; }

    IDictionary<string, string?>? Metadata { get; }

    ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null);

    object? Value { get; }
}
