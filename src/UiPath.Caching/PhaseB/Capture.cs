namespace UiPath.Caching.PhaseB;

/// <summary>The outcome of CAP: <see cref="Code"/> 0 with the token in force, -1 for another identity, -2 for a closed or absent gate.</summary>
internal readonly record struct Capture(long Code, string? Token, bool DestinationExists, string? Gate, long Tcap)
{
    public bool Captured => Code == 0;
}
