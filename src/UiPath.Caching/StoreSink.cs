namespace UiPath.Caching;

internal sealed class StoreSink
{
    private int _outcome;

    public StoreOutcome? Outcome => Volatile.Read(ref _outcome) is var stored and not 0 ? (StoreOutcome)(stored - 1) : null;

    public void Record(StoreOutcome outcome) => Volatile.Write(ref _outcome, (int)outcome + 1);
}
