using UiPath.Platform.Caching.Redis;

namespace UiPath.Platform.Caching.Broadcast;

public sealed class ChannelResolver : IChannelResolver
{
    private readonly string _channelPrefix;

    public ChannelResolver(IOptions<BroadcastOptions> optionsAccessor) =>
        _channelPrefix = optionsAccessor.Value.ChannelPrefix;

    public Channel GetFor(Type type, string key) =>
        CacheUtils.GetKey(type.Name, _channelPrefix);
}
