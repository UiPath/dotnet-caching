namespace UiPath.Caching.PhaseB;

/// <summary>Raised inside the claim store when a discarded transaction turned out to be a refused script, so the step takes the fallback.</summary>
internal sealed class ScriptsRefusedSignal : Exception
{
    public ScriptsRefusedSignal()
        : base("The claim script is refused; the step takes the fallback.")
    {
    }
}
