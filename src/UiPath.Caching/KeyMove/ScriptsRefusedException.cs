namespace UiPath.Caching.KeyMove;

/// <summary>Raised inside the claim store when a discarded transaction turned out to be a refused script, so the step takes the fallback.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3871:Exception types should be \"public\"", Justification = "Internal control-flow signal thrown and caught within the claim store; it never escapes the library.")]
internal sealed class ScriptsRefusedException : Exception
{
    public ScriptsRefusedException()
        : base("The claim script is refused; the step takes the fallback.")
    {
    }
}
