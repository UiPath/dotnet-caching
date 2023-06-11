namespace UiPath.Platform.Caching.Tests;

public class TestKeyResolver : IKeyResolver
{
    private readonly string _prefix;

    public TestKeyResolver(string prefix) =>
        _prefix = prefix;

    public TopicKey GetTopicKey(Type cacheEntryType, Type cacheType, string cacheName) =>
        InnerGet(_prefix, cacheType.Name, cacheEntryType.Name);

    public CacheKey GetInternalCacheKey(CacheKey originalCacheKey, Type cacheEntryType, Type cacheType, string cacheName) =>
        InnerGet(_prefix, cacheEntryType.Name, originalCacheKey);

    public string GetKey(string key, string? prefix = null) =>
        InnerGet(_prefix, prefix, key);

    private static string InnerGet(params string?[] str) =>
        string.Join(":", str.Where(s => !string.IsNullOrEmpty(s))).ToLowerInvariant();
}
