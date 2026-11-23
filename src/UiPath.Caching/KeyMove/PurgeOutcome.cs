namespace UiPath.Caching.KeyMove;

internal enum PurgeOutcome
{
    Purged,
    NoState,

    /// <summary>The group is not in S0, where N is empty by construction or stale.</summary>
    NotApplicable,

    /// <summary>The state moved, or a key's control record held another identity, while the purge ran.</summary>
    Refused,
}
