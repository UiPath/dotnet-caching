namespace UiPath.Caching.KeyMove;

internal enum ClaimDurability
{
    /// <summary>The step did not write (a code other than 1 or 2), so there was nothing to confirm.</summary>
    NotApplicable,

    Confirmed,

    /// <summary>The master has no replica: nothing to wait for, no alert on a single node.</summary>
    NoReplica,

    /// <summary>Replication remained unconfirmed because every WAIT returned zero, timed out, or failed. The caller waits when confirmation is required and otherwise raises <c>wait_skipped{claim}</c>.</summary>
    NotConfirmed,
}
