namespace UiPath.Caching.PhaseB;

internal enum ClaimCommandOutcome
{
    Done,

    /// <summary>The record changed since it was shown; re-read and confirm again.</summary>
    Changed,

    /// <summary>The group shows a terminal state; run <c>CertifyClaim</c> instead.</summary>
    NeedsCertify,

    /// <summary>No committed record shows a terminal state (or S5 without the sweep stored).</summary>
    NoEvidence,

    Refused,

    Unknown,
}
