namespace UiPath.Caching.PhaseB;

internal enum PreStep
{
    None,

    /// <summary>The owned-N census, a purge of any key it finds, and the census again (X3).</summary>
    NativeCensus,

    /// <summary>Sweep N before the flip to S0 (X10).</summary>
    SweepNative,
}
