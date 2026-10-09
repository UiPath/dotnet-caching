namespace UiPath.Caching.Broadcast;

/// <summary>Why a subject expires what its observers keep.</summary>
public enum MissedEventsReason
{
    /// <summary>Events are known lost, such as stream entries trimmed before this node read them.</summary>
    Lost,

    /// <summary>The subscription was not in place for a while, so events published then may not have arrived.</summary>
    SubscriptionGap,
}
