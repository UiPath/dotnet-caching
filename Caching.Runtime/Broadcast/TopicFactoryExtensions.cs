namespace UiPath.Platform.Caching.Broadcast;

public static class TopicFactoryExtensions
{
    public static ITopic<ICacheEvent> Get<T>(this ITopicFactory factory, string? providerName, TopicKey topicKey)
        => factory.Get(providerName, topicKey, typeof(T));

    public static ITopic<ICacheEvent> Get(this ITopicFactory factory, string? providerName, TopicKey topicKey, Type entryType)
    {
        var provider = factory.Get(providerName, entryType);
        return provider.CreateTopic(topicKey);
    }
}
