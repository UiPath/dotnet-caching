using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.Hybrid;

public class HybridCacheOptions : CacheOptions
{
    public string ChannelPrefix { get; set; } = "cache";

    public Uri? SourceUri { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);
}
