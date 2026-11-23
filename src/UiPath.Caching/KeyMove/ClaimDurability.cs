namespace UiPath.Caching.KeyMove;

internal enum ClaimDurability
{
    /// <summary>The step did not write (a code other than 1 or 2), so there was nothing to confirm.</summary>
    NotApplicable,

    Confirmed,

    /// <summary>The master has no replica: nothing to wait for, no alert on a single node.</summary>
    NoReplica,

    /// <summary>Every retry left WAIT at 0, timed out or failed: the caller raises <c>wait_skipped{claim}</c> or waits, by the design's rules.</summary>
    NotConfirmed,
}
