namespace UiPath.Caching;

public interface ICacheKeyStrategy
{
    /// <summary>Build the result with <see cref="CacheKey.WithName(string?)"/>, so a <see cref="CacheKeyCasing.Verbatim"/> or sensitive key keeps its mode; a new key takes the default casing.</summary>
    CacheKey GetCacheKey<T>(CacheKey key);
}
