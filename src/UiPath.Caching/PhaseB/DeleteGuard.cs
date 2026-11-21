namespace UiPath.Caching.PhaseB;

/// <summary>What a guarded delete checks on its guard record before it deletes.</summary>
internal enum DeleteGuard
{
    /// <summary>The record holds the identity; an absent record refuses.</summary>
    Identity,

    /// <summary>The record holds the identity or does not exist, for a group no transition ever reached.</summary>
    IdentityOrNoRecord,

    /// <summary>The record's <c>ep</c> is not above the given one, so a record of a newer identity survives.</summary>
    EpochAtMost,
}
