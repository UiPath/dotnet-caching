namespace UiPath.Platform.Caching;

internal record struct CacheEntryOptions : ICacheEntryOptions
{
    public CacheKey CacheKey { get; init; }

    public TopicKey TopicKey { get; init; }

    public CancellationToken Token { get; init; }

    public DateTimeOffset Expiration { get; set; }

    public IDictionary<string, string?>? Metadata { get; set; }
}
