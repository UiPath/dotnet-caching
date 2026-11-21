namespace UiPath.Caching.PhaseB;

internal enum BarrierOutcome
{
    /// <summary>WAIT counted one replica or more.</summary>
    Confirmed,

    /// <summary>The deployment is configured without replicas, ROLE shows none and none was ever seen: nothing to wait for.</summary>
    NoReplica,

    /// <summary>After the retries WAIT gave 0, timed out or failed; 0 proves nothing (a managed service in maintenance). Before step 4 the transition waits, after it <c>wait_skipped</c>.</summary>
    NotConfirmed,

    /// <summary>A way-back transition skipped the barrier at once: this master already failed one within the episode.</summary>
    SkippedAfterRecentFailure,
}
