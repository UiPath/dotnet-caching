namespace UiPath.Platform.Caching.Broadcast;

public static class TopicFactoryExtensions
{
    public static ITopic<ICacheEvent> Get<T>(this ITopicFactory factory, string? providerName, TopicKey topicKey)
    {
        var provider = factory.Get(providerName, typeof(T));
        return provider.CreateTopic(topicKey);
    }

    public static ITopic<ICacheEvent> Get(this ITopicFactory factory, string? providerName, TopicKey topicKey, Type entryType)
    {
        var provider = factory.Get(providerName, entryType);
        return provider.CreateTopic(topicKey);
    }
}
