namespace UiPath.Caching.PhaseB;

internal enum BarrierResult
{
    Confirmed,

    /// <summary>The replicas did not acknowledge in every try.</summary>
    Failed,

    /// <summary>A script refused the write, so there is nothing to confirm.</summary>
    Refused,
}
