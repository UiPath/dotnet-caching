namespace UiPath.Caching.PhaseB;

/// <summary>What made the server discard a bound claim transaction, as far as the claim store can work around it.</summary>
internal enum AbortCause
{
    /// <summary>CLIENT ID or INFO is refused: no step can be proven, so it runs unbound and is never confirmed.</summary>
    ProofDenied,

    /// <summary>The script is refused: the step takes the fallback.</summary>
    ScriptsRefused,
}
