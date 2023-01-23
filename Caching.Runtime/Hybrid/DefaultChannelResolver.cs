using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.Hybrid;

public sealed class DefaultChannelResolver : IChannelResolver
{
    private readonly string _channelPrefix;

    public DefaultChannelResolver(IOptions<HybridCacheOptions> optionsAccessor) =>
        _channelPrefix = optionsAccessor.Value.ChannelPrefix;

    public Channel GetFor(Type type, string key) =>
        CacheUtils.GetKey(type.Name, _channelPrefix);
}
