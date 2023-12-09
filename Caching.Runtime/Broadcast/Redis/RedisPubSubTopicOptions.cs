using System.Threading.Channels;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisPubSubTopicOptions
{
    public bool Enabled { get; set; }

    public IRedisChannelStrategy? RedisChannelStrategy { get; set; }

    public int ConsumerCapacity { get; set; } = 2048;

    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;
}
