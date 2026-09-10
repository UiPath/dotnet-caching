namespace UiPath.Caching;

public interface ICacheEntryOptions
{

    IDictionary<string, string?>? Metadata { get; }
    CacheKey CacheKey { get; }

    /// <summary>The key the caller passed, before <see cref="ICacheKeyStrategy"/> composed <see cref="CacheKey"/> from it; the composed key when a source has only that.</summary>
    CacheKey CallerKey => CacheKey;

    TopicKey TopicKey { get; }

    DateTimeOffset Expiration { get; }
}
