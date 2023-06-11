using System.Diagnostics.CodeAnalysis;

namespace UiPath.Platform.Caching.Broadcast;

[ExcludeFromCodeCoverage]
public sealed class NullTopicProvider : ITopicProvider
{
    public static readonly ITopicProvider Instance = new NullTopicProvider();

    public string Name => "Null";

    public bool Enabled { get; } = true;

    public ITopic<ICacheEvent> CreateTopic(TopicKey topicKey) =>
        NullTopic<ICacheEvent>.Instance;

    public void Dispose()
    {
        // Nothing to dispose
    }
}
