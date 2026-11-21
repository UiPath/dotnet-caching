namespace UiPath.Caching.PhaseB;

internal enum BarrierResult
{
    Confirmed,

    /// <summary>The replicas did not acknowledge in every try.</summary>
    Failed,

    /// <summary>A script refused the write, so there is nothing to confirm.</summary>
    Refused,

    /// <summary>The write went to another master; nothing was waited for and the caller issues it again on the owner.</summary>
    Misrouted,
}
