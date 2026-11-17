namespace UiPath.Caching.Tests;

internal class TestCacheEntryFactory : ICacheEntryFactory
{
    public ICacheEntry<T> Create<T>(T value, DateTimeOffset expiration, IDictionary<string, string?>? properties = default) =>
        new TestCacheEntry<T> { Expiration = expiration, Value = value, Metadata = properties };
}
