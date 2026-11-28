namespace UiPath.Caching.KeyMove;

internal enum ClaimCommandOutcome
{
    Done,

    /// <summary>Done, but a WAIT did not confirm a step; the caller raises <c>wait_skipped{claim}</c>.</summary>
    DoneUnconfirmed,

    /// <summary>A field of the group is live (pend or open).</summary>
    Live,

    /// <summary>The group is still in a build, or reached done without <c>lswept</c>.</summary>
    NotRemovable,

    /// <summary>The record changed since it was shown; re-read and confirm again.</summary>
    Changed,

    /// <summary>The group shows a terminal state; run <c>CertifyClaim</c> instead.</summary>
    NeedsCertify,

    /// <summary>No committed record shows a terminal state (or done without the sweep stored).</summary>
    NoEvidence,

    Refused,

    Unknown,
}
