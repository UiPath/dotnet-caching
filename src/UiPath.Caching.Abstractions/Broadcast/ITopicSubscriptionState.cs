namespace UiPath.Caching.Broadcast;

/// <summary>Whether the topic's subscription is in place now; it says nothing of losses, which <c>IMissedEventsObserver</c> reports, and a decorator of a topic must forward it.</summary>
public interface ITopicSubscriptionState
{
    bool IsSubscribed { get; }
}
