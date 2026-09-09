namespace UiPath.Caching;

internal record struct CacheEntryOptions : ICacheEntryOptions
{
    private readonly CacheKey _callerKey;

    public CacheKey CacheKey { get; init; }

    /// <summary>The key the caller passed, before <see cref="ICacheKeyStrategy"/> composed <see cref="CacheKey"/> from it.</summary>
    public CacheKey CallerKey
    {
        get => _callerKey.IsNull ? CacheKey : _callerKey;
        init => _callerKey = value;
    }

    public TopicKey TopicKey { get; init; }

    public CancellationToken Token { get; init; }

    public DateTimeOffset Expiration { get; set; }

    public IDictionary<string, string?>? Metadata { get; set; }
}
