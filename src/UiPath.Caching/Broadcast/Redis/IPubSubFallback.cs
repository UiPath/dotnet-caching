namespace UiPath.Caching.Broadcast.Redis;

/// <summary>The Pub/Sub provider as the Streams topics fall back to it, whether or not it is the one selected to broadcast.</summary>
internal interface IPubSubFallback
{
    /// <summary>The topic for the key, shared with whoever else asks for it; the caller never disposes it.</summary>
    ITopic<ICacheEvent> Create(TopicKey topicKey);
}
