namespace UiPath.Platform.Caching.Broadcast.Redis;

public interface IRedisStreamKeyStrategy
{
    RedisKey GetRedisKey(TopicKey topicKey);
}
