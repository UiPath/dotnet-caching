namespace UiPath.Platform.Caching.Broadcast;

public sealed class DefaultTopicKeyStrategy : ITopicKeyStrategy
{
    public TopicKey GetTopicKey<T>() => typeof(T).Name;
}
