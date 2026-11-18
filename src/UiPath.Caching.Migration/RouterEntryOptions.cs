using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration;

internal sealed class RouterEntryOptions(string key, TopicKey topic, DateTimeOffset expiration) : ICacheEntryOptions
{
    public IDictionary<string, string?>? Metadata => null;

    public CacheKey CacheKey { get; } = CacheKey.Verbatim(key);

    public TopicKey TopicKey => topic;

    public DateTimeOffset Expiration => expiration;
}
