namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisPubSubTopicOptions
{
    public bool Enabled { get; set; } = true;

    public IRedisChannelStrategy? RedisChannelStrategy { get; set; }
}
