namespace UiPath.Platform.Caching.Broadcast;

public interface ITopicProvider : IDisposable
{
    string Name { get; }

    bool Enabled { get; }

    ICollection<TopicKey> Keys { get; }

    ITopic<ICacheEvent> Create(TopicKey topicKey);

    void Remove(TopicKey topicKey);
}
