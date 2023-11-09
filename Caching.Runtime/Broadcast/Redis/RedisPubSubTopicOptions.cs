using System.Threading.Channels;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisPubSubTopicOptions
{
    public bool Enabled { get; set; } = true;

    public IRedisChannelStrategy? RedisChannelStrategy { get; set; }

    public int ConsumerCapacity { get; set; } = -1;

    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;
}
