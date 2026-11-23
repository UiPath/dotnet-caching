namespace UiPath.Caching.KeyMove;

/// <summary>One admitted unit of work of a group: counted under the identity it was admitted under, until it completes or is re-admitted under a newer one.</summary>
internal sealed class AdmittedUnit : IDisposable
{
    private readonly EpochGate _gate;
    private readonly string _group;
    private readonly bool _promotion;
    private int _done;

    internal AdmittedUnit(EpochGate gate, string group, long ep, string id, bool promotion)
    {
        (_gate, _group, Ep, Id, _promotion) = (gate, group, ep, id, promotion);
    }

    public long Ep { get; private set; }

    public string Id { get; private set; }

    /// <summary>Counts the unit under the identity now in force, after a script returned -1; false when the identity did not change, so the unit is not re-run.</summary>
    public bool Readmit()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _done) == 1, this);

        return _gate.Move(this, _group, _promotion);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _done, 1) == 0)
        {
            _gate.Leave(_group, Ep, Id, _promotion);
        }
    }

    internal void Rebind(long ep, string id) => (Ep, Id) = (ep, id);
}
