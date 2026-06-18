namespace UiPath.Caching.Broadcast.Redis;

public interface IRedisStreamKeyStrategy
{
    RedisKey GetRedisKey(TopicKey topicKey);
}
