namespace UiPath.Caching.Broadcast.Redis;

/// <summary>What the Streams topic does when the server does not run the Streams commands it needs.</summary>
public enum StreamsUnsupportedAction
{
    /// <summary>Broadcast over Pub/Sub instead, with one warning.</summary>
    PubSub,

    /// <summary>Turn broadcast off for the topic, with one warning.</summary>
    Disable,

    /// <summary>Fail host start when a probe within its budget proves the commands missing; after start, keep the topic as it is and log one error.</summary>
    Fail,
}
