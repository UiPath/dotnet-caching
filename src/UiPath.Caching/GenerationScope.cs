namespace UiPath.Caching;

/// <summary>What a generation runs under: its token, the shared run when it has one, so it can tell that every caller has left and not store over a newer value, and the run's end when this frame is the one that ends it.</summary>
internal readonly record struct GenerationScope(IInFlightRun? Run, CancellationToken Token, IInFlightEnd<object?>? End = null)
{
    public bool IsAbandoned => Run?.IsAbandoned == true;

    /// <summary>The scope for a frame inside the one that ends the run, so the inner one does not end it first.</summary>
    public GenerationScope Inner => this with { End = null };

    /// <summary>Ends the run with <paramref name="result"/>, when this frame ends it: out of the table before the caller that started it sees the result.</summary>
    public void Complete(object? result) => End?.Complete(result);

    /// <summary>Ends the run with <paramref name="failure"/>, when this frame ends it.</summary>
    public void Fail(Exception failure) => End?.Fail(failure);
}
