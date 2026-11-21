namespace UiPath.Caching.Migration;

/// <summary>One admitted operation, counted for the stop and for the drain of its mode, and for the drain of its group's pin when it may reach the source.</summary>
internal readonly struct Admission : IDisposable
{
    private readonly RouterCore _core;
    private readonly GroupPin? _pin;

    public Admission(RouterCore core, int mode, GroupPin? pin = null)
    {
        _core = core;
        Mode = mode;
        _pin = pin;
    }

    public int Mode { get; }

    public Admission WithPin(GroupPin pin) => new(_core, Mode, pin);

    public void Dispose()
    {
        _core.Exit(Mode);
        _pin?.EndSource();
    }
}
