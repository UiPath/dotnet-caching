namespace UiPath.Platform.Caching.Broadcast;
public interface ITopicKeyStrategy
{
    TopicKey GetTopicKey<T>();
}
