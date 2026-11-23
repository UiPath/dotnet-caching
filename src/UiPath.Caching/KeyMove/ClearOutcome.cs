namespace UiPath.Caching.KeyMove;

internal enum ClearOutcome
{
    Cleared,

    /// <summary>A transition is in flight, another Clear holds the lock, or a gate could not be closed: the caller retries.</summary>
    Unavailable,

    /// <summary>The sweep could not be made safe: the gates were replaced on every retry, the lock was lost, or the sweep failed.</summary>
    Failed,
}
