namespace UiPath.Platform.Caching;

public interface ICacheEntry
{
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
