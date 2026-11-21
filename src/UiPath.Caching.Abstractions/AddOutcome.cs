namespace UiPath.Caching;

/// <summary>What <see cref="IMigrationCache.TryCreateAsync{T}"/> did.</summary>
public enum AddOutcome
{
    /// <summary>Anything else; <c>default</c>, so an unconfigured mock never reads as a success.</summary>
    Failed = 0,

    /// <summary>Refused before any byte left: cancelled, not connected, or the key or value failed before sending.</summary>
    NotSent,

    Added,

    /// <summary>The key held a value, which was left as it was.</summary>
    Exists,

    /// <summary>Sent, but the store did not answer: a timeout or a connection lost after the send.</summary>
    Unknown,
}
