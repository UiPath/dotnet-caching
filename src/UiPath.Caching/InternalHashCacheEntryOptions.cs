namespace UiPath.Caching;

internal record struct InternalHashCacheEntryOptions : ILayeredOptions<InternalHashCacheEntryOptions>
{
    private readonly CacheKey _callerKey;

    public CacheKey CacheKey { get; init; }

    /// <summary>The key the caller passed, before <see cref="ICacheKeyStrategy"/> composed <see cref="CacheKey"/> from it.</summary>
    public CacheKey CallerKey
    {
        get => _callerKey.IsNull ? CacheKey : _callerKey;
        init => _callerKey = value;
    }

    public string[]? Fields { get; init; }

    public TopicKey TopicKey { get; init; }

    public CancellationToken Token { get; init; }
    
    public HashCacheSetOption SetOption { get; init; }

    /// <summary>The fields of this write are merged into a hash that holds others, so they are not the whole hash.</summary>
    public bool Partial { get; init; }

    /// <summary>The count of the hash's writes the entry is the state of; zero when it is not known.</summary>
    public long Version { get; set; }

    /// <summary>The background write whose fields the entry is made of; its version is told once it returns.</summary>
    public OwnWrite? Pending { get; set; }

    /// <summary>The writes of this node laid over <see cref="Version"/> in the entry being kept.</summary>
    public OwnWrite[]? Own { get; set; }

    public DateTimeOffset Expiration { get; set; }

    public IDictionary<string, string?>? Metadata { get; set; }

    public InternalHashCacheEntryOptions WithToken(CancellationToken token) => this with { Token = token };
}
