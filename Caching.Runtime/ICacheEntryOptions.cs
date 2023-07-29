namespace UiPath.Platform.Caching;

internal interface ICacheEntryOptions
{
    CacheKey CacheKey { get; }

    TopicKey TopicKey { get; }

    DateTimeOffset Expiration { get; }

    public IDictionary<string, string?>? Metadata { get; }
}
