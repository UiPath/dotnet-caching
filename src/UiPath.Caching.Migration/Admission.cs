namespace UiPath.Caching.Migration;

/// <summary>One admitted operation, counted for the stop and for the drain of its mode.</summary>
internal readonly struct Admission : IDisposable
{
    private readonly RouterCore _core;

    public Admission(RouterCore core, int mode)
    {
        _core = core;
        Mode = mode;
    }

    public int Mode { get; }

    public void Dispose() => _core.Exit(Mode);
}
