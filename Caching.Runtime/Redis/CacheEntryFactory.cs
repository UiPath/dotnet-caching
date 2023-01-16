namespace UiPath.Platform.Caching.Redis;

public class CacheEntryFactory : ICacheEntryFactory
{
    public ICacheEntry<T> Create<T>(T value, DateTimeOffset expiration, IDictionary<string, string?>? properties = null) =>
        new CacheEntry<T>(value, expiration, properties);

    private sealed record CacheEntry<T> : ICacheEntry<T>
    {
        public CacheEntry(T? value, DateTimeOffset? expiration = null, IDictionary<string, string?>? extendedProperties = null)
        {
            Value = value;
            Expiration = expiration ?? DateTimeOffset.MaxValue;
            ExtendedProperties = extendedProperties;
        }

        public T? Value { get; private set; }

        public DateTimeOffset Expiration { get; private set; }

        public IDictionary<string, string?>? ExtendedProperties { get; private set; }

        public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? extendedProperties = null) =>
            new CacheEntry<T>(Value, expiration, extendedProperties);
    }
}
