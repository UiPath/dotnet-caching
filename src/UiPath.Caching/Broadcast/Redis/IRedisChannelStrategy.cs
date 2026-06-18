namespace UiPath.Caching.Broadcast.Redis;

public interface IRedisChannelStrategy
{
    RedisChannel GetRedisChannel(TopicKey topicKey);
}

