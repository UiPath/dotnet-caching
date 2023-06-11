namespace UiPath.Platform.Caching.Broadcast;

public interface ITopicProvider : IDisposable
{
    string Name { get; }

    bool Enabled { get; }

    ITopic<ICacheEvent> CreateTopic(TopicKey topicKey);
}

