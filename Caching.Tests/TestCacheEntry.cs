namespace UiPath.Platform.Caching.Tests;

internal class TestCacheEntry<T> : ICacheEntry<T>
{
    public T? Value { get; set; }

    public DateTimeOffset Expiration { get; set; } = DateTimeOffset.MaxValue;

    public IDictionary<string, string?>? Metadata { get; set; }

    public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) =>
        new TestCacheEntry<T>
        {
            Value = Value,
            Expiration = expiration ?? DateTimeOffset.MaxValue,
            Metadata = metadata
        };
}
