namespace UiPath.Caching.Redis;

internal sealed class UnreadableEntry<T>(ICacheEntry<T> miss) : ICacheEntry<T>, IUnreadableEntry
{
    public DateTimeOffset Expiration => miss.Expiration;

    public IDictionary<string, string?>? Metadata => miss.Metadata;

    public T? Value => miss.Value;

    object? ICacheEntry.Value => miss.Value;

    public bool Found => miss.Found;

    public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) => miss.NewEntry(expiration, metadata);
}
