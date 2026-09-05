namespace UiPath.Caching;

public interface ICacheEntry
{
    /// <summary>
    /// When the entry leaves storage. <see cref="DateTimeOffset.MaxValue"/> means the key has no TTL
    /// there; it is what storage reported, not a default the cache filled in.
    /// </summary>
    DateTimeOffset Expiration { get; }

    IDictionary<string, string?>? Metadata { get; }

    ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null);

    object? Value { get; }

    /// <summary>
    /// True when the entry represents a cache hit (stored value or explicitly cached null). Default
    /// implementation: <see cref="Expiration"/> &gt; <see cref="DateTimeOffset.MinValue"/>. Override
    /// if your implementation populates a real expiration on miss.
    /// </summary>
    bool Found => Expiration > DateTimeOffset.MinValue;
}
