namespace UiPath.Caching.PhaseB;

internal enum ScriptSupport
{
    Scripts,

    /// <summary>Scripts are refused and the server can run the watched-transaction fallback.</summary>
    Fallback,

    Unsupported,
}
