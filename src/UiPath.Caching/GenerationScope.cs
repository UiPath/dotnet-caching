namespace UiPath.Caching;

/// <summary>What a generation runs under: its token, and the shared run when it has one, so it can tell that every caller has left and not store over a newer value.</summary>
internal readonly record struct GenerationScope(CancellationToken Token, IInFlightRun? Run)
{
    public bool IsAbandoned => Run?.IsAbandoned == true;
}
