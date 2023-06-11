using System.Text;

namespace UiPath.Platform.Caching;

public class KeyResolver : IKeyResolver
{
    private readonly char _separator;

    public KeyResolver(IOptions<CacheOptions> options) =>
        _separator = options.Value.Separator;

    public TopicKey GetTopicKey(Type cacheEntryType, Type cacheType, string cacheName) =>
        cacheEntryType.Name.ToLowerInvariant();

    public CacheKey GetInternalCacheKey(CacheKey originalCacheKey, Type cacheEntryType, Type cacheType, string cacheName) =>
        originalCacheKey;

    public string GetKey(string key, string? prefix = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (string.IsNullOrWhiteSpace(prefix))
        {
            return key.ToLowerInvariant();
        }
        var sb = new StringBuilder(prefix);
        sb.Append(_separator);
        sb.Append(key);
        return sb.ToString().ToLowerInvariant();
    }
}
