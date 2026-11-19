namespace UiPath.Caching;

public interface ICacheKeyStrategy
{
    /// <summary>Build the result with <see cref="CacheKey.WithName(string?)"/>, so a <see cref="CacheKeyCasing.Verbatim"/> or sensitive key keeps its mode; a new key takes the default casing. A strategy that returns <see cref="CacheKeyCasing.Verbatim"/> keys must not implement <see cref="ISpanCacheKeyStrategy"/>, or must decline every call, since the span path normalizes.</summary>
    CacheKey GetCacheKey<T>(CacheKey key);
}
