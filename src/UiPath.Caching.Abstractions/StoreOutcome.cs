namespace UiPath.Caching;

/// <summary>What <see cref="IMigrationCache.WriteAsync{T}"/> or <see cref="IMigrationCache.DeleteAsync{T}"/> did.</summary>
public enum StoreOutcome
{
    /// <summary>Anything else; <c>default</c>, so an unconfigured mock never reads as a success.</summary>
    Failed = 0,

    /// <summary>Refused before any byte left: cancelled, not connected, or the key or value failed before sending.</summary>
    NotSent,

    Committed,

    /// <summary>Sent, but the store did not answer: a timeout or a connection lost after the send.</summary>
    Unknown,
}
