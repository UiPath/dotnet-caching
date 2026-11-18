namespace UiPath.Caching.Broadcast;

/// <summary>Implemented by a topic that can say whether its subscription is in place; events published while it is not are missed.</summary>
public interface ITopicSubscriptionState
{
    bool IsSubscribed { get; }
}
