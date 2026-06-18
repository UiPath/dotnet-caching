namespace UiPath.Caching.Broadcast;
public interface ITopicKeyStrategy
{
    TopicKey GetTopicKey(Type topicType);

    TopicKey GetTopicKey<T>() => GetTopicKey(typeof(T));
}
