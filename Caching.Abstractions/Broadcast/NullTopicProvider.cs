using System.Diagnostics.CodeAnalysis;

namespace UiPath.Platform.Caching.Broadcast;

[ExcludeFromCodeCoverage]
public sealed class NullTopicProvider : ITopicProvider
{
    public static readonly ITopicProvider Instance = new NullTopicProvider();

    public string Name => "Null";

    public bool Enabled { get; } = true;
    public ICollection<TopicKey> Keys => Array.Empty<TopicKey>();

    public ITopic<ICacheEvent> Create(TopicKey topicKey) =>
        NullTopic<ICacheEvent>.Instance;

    public void Remove(TopicKey topicKey)
    {
        // Nothing to remove
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
