namespace UiPath.Platform.Caching;

public interface ICacheEntryFactory
{
    ICacheEntry<T> Create<T>(T value, DateTimeOffset expiration, IDictionary<string, string?>? properties = default);
}
